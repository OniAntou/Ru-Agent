using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using RuAgent.Hosting;
using RuAgent.Protocol.Harness;

namespace RuBackend.Hosting;

// Managed runtime bootstrap BEFORE DI: resolve paths, check port conflict,
// acquire single instance, generate credentials, start Broker, publish
// descriptor. Moved verbatim from Program.cs so startup order is unchanged.
internal sealed record ManagedRuntimeBootstrap(
    string? PublicBearer,
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

    internal static async Task<ManagedRuntimeBootstrap> StartAsync(IConfiguration configuration, string projectRoot)
    {
        string? managedPublicBearer = null;

        var configDataRoot = configuration["RU_DATA_ROOT"] ?? Environment.GetEnvironmentVariable("RU_DATA_ROOT");
        var configLogRoot = configuration["RU_LOG_ROOT"] ?? Environment.GetEnvironmentVariable("RU_LOG_ROOT");
        // Resolve descriptor path via hosting options
        var descriptorPath = RuAgent.Hosting.PublicHostRuntimeOptions.ResolveDescriptorPath(configDataRoot, configLogRoot, projectRoot);
        var managedDescriptorWriter = new RuAgent.Hosting.RuntimeDescriptorWriter(descriptorPath);

        // Determine public port from BindUrl. The managed launcher passes --urls;
        // reading it here keeps the conflict/single-instance gate on the same port
        // the host will actually bind instead of a stale default.
        var bindUrl = configuration["RuBackend:BindUrl"]
            ?? configuration["urls"]
            ?? RuAgent.Hosting.PublicHostRuntimeOptions.DefaultBindUrl;
        int publicPort = new Uri(RuAgent.Hosting.PublicHostRuntimeOptions.DefaultBindUrl).Port;
        if (Uri.TryCreate(bindUrl, UriKind.Absolute, out var bindUri))
            publicPort = bindUri.Port;

        // Port conflict check: if port occupied and no valid owned descriptor, fail fast
        if (IsPortOccupied(publicPort) && managedDescriptorWriter.IsStale())
        {
            throw new HostStartupException("{\"error_code\":\"public_host_port_conflict\"}", 2);
        }

        // The single instance is the shared data root, not only the port: a host
        // on another port would publish its runtime descriptor over the same
        // path, so the root is claimed before the Broker starts and held for the
        // host lifetime. A held lock fails closed; only a namespace this account
        // cannot create falls back to Local.
        var effectiveDataRoot = string.IsNullOrWhiteSpace(configDataRoot)
            ? PublicHostRuntimeOptions.GetDefaultBusinessRoot(projectRoot)
            : configDataRoot;
        var managedSingleInstance = RuAgent.Hosting.PublicHostSingleInstance.AcquireForPortAndDataRoot(
            publicPort,
            effectiveDataRoot);
        if (!managedSingleInstance.IsAcquired)
        {
            managedSingleInstance.Dispose();
            throw new HostStartupException("{\"error_code\":\"public_host_already_running\"}", 3);
        }

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

        // Web tool configuration is forwarded to the Broker process (the execution
        // authority) through the environment before the child starts.
        // Web egress requires explicit approval by default: read-only risk class
        // alone must never auto-allow traffic outside the loopback boundary.
        Environment.SetEnvironmentVariable("RU_AGENT_BROKER_WEB_ENABLED", "true");
        Environment.SetEnvironmentVariable("RU_AGENT_BROKER_WEB_SEARCH_ENDPOINT", BrokerWebSearchEndpoint);
        Environment.SetEnvironmentVariable("RU_AGENT_BROKER_WEB_REQUIRE_APPROVAL", "true");

        // Start the Broker in its own Job
        var managedWorkerBrokerManager = new RuAgent.Hosting.WorkerBrokerProcessManager();
        var startup = new WorkerBrokerStartupOptions(
            null,
            Environment.GetEnvironmentVariable("RU_AGENT_BROKER_EXE"),
            publicPort);
        var brokerStarted = await managedWorkerBrokerManager
            .StartAsync(projectRoot, startup)
            .ConfigureAwait(false);
        if (!brokerStarted)
        {
            throw new HostStartupException($"Broker start failed: {managedWorkerBrokerManager.LastError}", 4);
        }

        // Propagate broker descriptor to Host's env for Broker client
        var brokerDescriptorPath = managedWorkerBrokerManager.BrokerDescriptorPath;
        if (!string.IsNullOrWhiteSpace(brokerDescriptorPath))
        {
            Environment.SetEnvironmentVariable("RU_AGENT_BROKER_DESCRIPTOR", brokerDescriptorPath);
            configuration["RU_AGENT_BROKER_DESCRIPTOR"] = brokerDescriptorPath;
        }

        // Publish ACL-restricted descriptor after the Broker is running and before
        // health is available. A valid descriptor at this point belongs to a live
        // instance that holds the lock elsewhere; refuse to clobber it.
        var proc = Process.GetCurrentProcess();
        var descriptor = new RuntimeDescriptor(
            RuntimeDescriptor.CurrentProtocolVersion,
            $"http://127.0.0.1:{publicPort}/",
            managedPublicBearer,
            proc.Id,
            proc.StartTime.ToUniversalTime().ToString("O"),
            RuAgent.Hosting.PublicHostRuntimeOptions.GenerateNonce(),
            DateTimeOffset.UtcNow.ToString("O"));
        if (!await managedDescriptorWriter.TryWriteIfUnclaimedAsync(descriptor).ConfigureAwait(false))
        {
            throw new HostStartupException("{\"error_code\":\"public_host_already_running\"}", 3);
        }

        return new ManagedRuntimeBootstrap(
            managedPublicBearer,
            managedWorkerBrokerManager,
            managedDescriptorWriter,
            managedSingleInstance);
    }

    private static bool IsPortOccupied(int port)
        => RuAgent.Hosting.PortProbe.IsLoopbackPortOccupied(port);
}
