using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using RuAgent.Hosting;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuBackend.Hosting;
using RuBackend.Runtime;

namespace RuBackend.Hosting;

// Managed runtime bootstrap BEFORE DI: resolve paths, check port conflict,
// acquire single instance, generate credentials, start Worker/Broker, publish
// descriptor. Moved verbatim from Program.cs so startup order is unchanged.
internal sealed record ManagedRuntimeBootstrap(
    string? PublicBearer,
    int AgentWorkerPort,
    int DeviceWorkerPort,
    string AgentWorkerToken,
    string DeviceWorkerToken,
    WorkerBrokerProcessManager WorkerBrokerManager,
    RuntimeDescriptorWriter DescriptorWriter,
    PublicHostSingleInstance SingleInstance);

internal sealed class HostStartupException : Exception
{
    public int ExitCode { get; }

    public HostStartupException(string message, int exitCode) : base(message)
    {
        ExitCode = exitCode;
    }
}

internal static class ManagedRuntimeBootstrapper
{
    // Web tool configuration is defined once and shared: the Host executor uses
    // it directly, the Broker process receives it through the environment.
    public const string BrokerWebSearchEndpoint = "https://www.google.com/search";
    public const string BrokerWebFallbackEndpoint = "https://html.duckduckgo.com/html/";

    internal static async Task<ManagedRuntimeBootstrap> StartAsync(IConfiguration configuration, string projectRoot)
    {
        string? managedPublicBearer = null;
        var managedAgentWorkerToken = string.Empty;
        var managedDeviceWorkerToken = string.Empty;
        var managedAgentWorkerPort = PublicHostRuntimeOptions.DefaultWorkerPort;
        var managedDeviceWorkerPort = PublicHostRuntimeOptions.DefaultDeviceWorkerPort;

        var configDataRoot = configuration["RU_DATA_ROOT"] ?? Environment.GetEnvironmentVariable("RU_DATA_ROOT");
        var configLogRoot = configuration["RU_LOG_ROOT"] ?? Environment.GetEnvironmentVariable("RU_LOG_ROOT");
        // Resolve descriptor path via hosting options
        var descriptorPath = RuAgent.Hosting.PublicHostRuntimeOptions.ResolveDescriptorPath(configDataRoot, configLogRoot, projectRoot);
        var managedDescriptorWriter = new RuAgent.Hosting.RuntimeDescriptorWriter(descriptorPath);

        // Determine public port from BindUrl
        var bindUrl = configuration["RuBackend:BindUrl"] ?? "http://127.0.0.1:8000";
        int publicPort = 8000;
        if (Uri.TryCreate(bindUrl, UriKind.Absolute, out var bindUri))
            publicPort = bindUri.Port;

        // Port conflict check: if port occupied and no valid owned descriptor, fail fast
        if (IsPortOccupied(publicPort) && managedDescriptorWriter.IsStale())
        {
            throw new HostStartupException("{\"error_code\":\"public_host_port_conflict\"}", 2);
        }

        // Single instance mutex (global per port)
        var managedSingleInstance = new RuAgent.Hosting.PublicHostSingleInstance($"Global\\RuPublicHost-{publicPort}");
        if (!managedSingleInstance.IsAcquired)
        {
            // Try Local fallback
            managedSingleInstance.Dispose();
            managedSingleInstance = new RuAgent.Hosting.PublicHostSingleInstance($"Local\\RuPublicHost-{publicPort}");
            if (!managedSingleInstance.IsAcquired)
            {
                throw new HostStartupException("{\"error_code\":\"public_host_already_running\"}", 3);
            }
        }

        // Generate independent private credentials for the Agent and device workers.
        managedAgentWorkerToken = RuAgent.Hosting.PublicHostRuntimeOptions.GenerateBearerToken();
        managedDeviceWorkerToken = RuAgent.Hosting.PublicHostRuntimeOptions.GenerateBearerToken();
        managedPublicBearer = configuration["RU_DESKTOP_TOKEN"] ?? Environment.GetEnvironmentVariable("RU_DESKTOP_TOKEN");
        if (string.IsNullOrWhiteSpace(managedPublicBearer))
        {
            managedPublicBearer = RuAgent.Hosting.PublicHostRuntimeOptions.GenerateBearerToken();
            Environment.SetEnvironmentVariable("RU_DESKTOP_TOKEN", managedPublicBearer);
            configuration["RU_DESKTOP_TOKEN"] = managedPublicBearer;
            configuration["RuBackend:DesktopToken"] = managedPublicBearer;
        }
        var configuredLogRoot = configuration["RU_LOG_ROOT"]
            ?? Environment.GetEnvironmentVariable("RU_LOG_ROOT");
        var managedLogRoot = PublicHostRuntimeOptions.ResolveLogRoot(projectRoot, configuredLogRoot);
        Environment.SetEnvironmentVariable("RU_LOG_ROOT", managedLogRoot);
        configuration["RU_LOG_ROOT"] = managedLogRoot;

        // Resolve ports before creating the protected bootstrap. The manager uses the
        // same log/session root and returns the final values after readiness succeeds.
        managedAgentWorkerPort = ReadConfiguredPort(
            configuration["RuBackend:AgentWorkerPort"]
                ?? Environment.GetEnvironmentVariable("RU_BACKEND_AGENT_WORKER_PORT")
                ?? Environment.GetEnvironmentVariable("RU_BACKEND_WORKER_PORT"),
            PublicHostRuntimeOptions.DefaultWorkerPort);
        managedDeviceWorkerPort = ReadConfiguredPort(
            configuration["RuBackend:DeviceWorkerPort"]
                ?? Environment.GetEnvironmentVariable("RU_BACKEND_DEVICE_WORKER_PORT"),
            PublicHostRuntimeOptions.DefaultDeviceWorkerPort);

        var bootstrapRoot = Path.Combine(managedLogRoot, "agent", "session");
        var providerBootstrapPath = await new ManagedProviderBootstrapFactory()
            .CreateAsync(
                bootstrapRoot,
                CancellationToken.None)
            .ConfigureAwait(false);

        // Web tool configuration is forwarded to the Broker process (the execution
        // authority) through the environment before the children start.
        // Web egress requires explicit approval by default: read-only risk class
        // alone must never auto-allow traffic outside the loopback boundary.
        Environment.SetEnvironmentVariable("RU_AGENT_BROKER_WEB_ENABLED", "true");
        Environment.SetEnvironmentVariable("RU_AGENT_BROKER_WEB_SEARCH_ENDPOINT", BrokerWebSearchEndpoint);
        Environment.SetEnvironmentVariable("RU_AGENT_BROKER_WEB_FALLBACK_ENDPOINT", BrokerWebFallbackEndpoint);
        Environment.SetEnvironmentVariable("RU_AGENT_BROKER_WEB_REQUIRE_APPROVAL", "true");

        // Start Worker and Broker in one Job
        var managedWorkerBrokerManager = new RuAgent.Hosting.WorkerBrokerProcessManager();
        var startup = new WorkerBrokerStartupOptions(
            managedAgentWorkerToken,
            managedDeviceWorkerToken,
            providerBootstrapPath,
            managedAgentWorkerPort,
            managedDeviceWorkerPort,
            null,
            Environment.GetEnvironmentVariable("RU_AGENT_BROKER_EXE"));
        var workerStarted = await managedWorkerBrokerManager
            .StartAsync(projectRoot, startup)
            .ConfigureAwait(false);
        if (!workerStarted)
        {
            if (!string.IsNullOrWhiteSpace(providerBootstrapPath))
            {
                try
                {
                    ProtectedProviderBootstrap.Delete(bootstrapRoot, providerBootstrapPath);
                }
                catch
                {
                    // Startup must still fail closed; do not expose cleanup details.
                }
            }

            throw new HostStartupException($"Worker/Broker start failed: {managedWorkerBrokerManager.LastError}", 4);
        }

        managedAgentWorkerPort = managedWorkerBrokerManager.AgentWorkerPort;
        managedDeviceWorkerPort = managedWorkerBrokerManager.DeviceWorkerPort;

        // Propagate broker descriptor to Host's env for Broker client
        var brokerDescriptorPath = managedWorkerBrokerManager.BrokerDescriptorPath;
        if (!string.IsNullOrWhiteSpace(brokerDescriptorPath))
        {
            Environment.SetEnvironmentVariable("RU_AGENT_BROKER_DESCRIPTOR", brokerDescriptorPath);
            configuration["RU_AGENT_BROKER_DESCRIPTOR"] = brokerDescriptorPath;
        }

        // Publish ACL-restricted descriptor after children are running and before health is available
        var proc = Process.GetCurrentProcess();
        var descriptor = new RuntimeDescriptor(
            RuntimeDescriptor.CurrentProtocolVersion,
            $"http://127.0.0.1:{publicPort}/",
            managedPublicBearer,
            proc.Id,
            proc.StartTime.ToUniversalTime().ToString("O"),
            RuAgent.Hosting.PublicHostRuntimeOptions.GenerateNonce(),
            DateTimeOffset.UtcNow.ToString("O"));
        await managedDescriptorWriter.WriteAsync(descriptor).ConfigureAwait(false);

        return new ManagedRuntimeBootstrap(
            managedPublicBearer,
            managedAgentWorkerPort,
            managedDeviceWorkerPort,
            managedAgentWorkerToken,
            managedDeviceWorkerToken,
            managedWorkerBrokerManager,
            managedDescriptorWriter,
            managedSingleInstance);
    }

    private static bool IsPortOccupied(int port)
    {
        try { using var l = new TcpListener(IPAddress.Loopback, port); l.Start(); l.Stop(); return false; }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse) { return true; }
        catch { return false; }
    }

    internal static int ReadConfiguredPort(string? raw, int fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return int.TryParse(raw, out var parsed) ? parsed : fallback;
    }
}
