using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using RuAgent.Hosting;
using RuAgent.Protocol.Harness;

namespace RuBackend.Hosting;

// Managed runtime bootstrap BEFORE DI: resolve paths, check port conflict,
// acquire single instance, generate credentials, publish descriptor. Moved
// verbatim from Program.cs so startup order is unchanged. The execution
// authority itself is composed in DI (InProcessExecutionAuthority); this
// bootstrap no longer starts or owns any child process.
internal sealed record ManagedRuntimeBootstrap(
    string? PublicBearer,
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
    // Web tool configuration is defined once and shared: the in-process
    // execution authority receives it through the environment and parses it
    // with the Broker child's rules.
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

        // The single instance is the shared data root and log root, not only the
        // port: a host on another port would publish its runtime descriptor over
        // the same data root, and a host with another data root would host its
        // in-process execution session under the same log root. Both roots are
        // claimed before the descriptor is published and held for the host
        // lifetime. A held lock fails closed; only a namespace this account
        // cannot create falls back to Local.
        var effectiveDataRoot = string.IsNullOrWhiteSpace(configDataRoot)
            ? PublicHostRuntimeOptions.GetDefaultBusinessRoot(projectRoot)
            : configDataRoot;
        var managedLogRoot = PublicHostRuntimeOptions.ResolveLogRoot(projectRoot, configLogRoot);
        var managedSingleInstance = RuAgent.Hosting.PublicHostSingleInstance.AcquireForPortAndDataRoot(
            publicPort,
            effectiveDataRoot,
            managedLogRoot);
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
        Environment.SetEnvironmentVariable("RU_LOG_ROOT", managedLogRoot);
        configuration["RU_LOG_ROOT"] = managedLogRoot;

        // Web tool configuration is defined once and shared: the Host's
        // in-process execution authority reads these variables with the Broker
        // child's parsing rules at composition time. Web egress requires
        // explicit approval by default: read-only risk class alone must never
        // auto-allow traffic outside the loopback boundary.
        Environment.SetEnvironmentVariable("RU_AGENT_BROKER_WEB_ENABLED", "true");
        Environment.SetEnvironmentVariable("RU_AGENT_BROKER_WEB_SEARCH_ENDPOINT", BrokerWebSearchEndpoint);
        Environment.SetEnvironmentVariable("RU_AGENT_BROKER_WEB_REQUIRE_APPROVAL", "true");

        // Publish ACL-restricted descriptor before health is available. A valid
        // descriptor at this point belongs to a live instance that holds the
        // lock elsewhere; refuse to clobber it.
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
            managedDescriptorWriter,
            managedSingleInstance);
    }

    private static bool IsPortOccupied(int port)
        => RuAgent.Hosting.PortProbe.IsLoopbackPortOccupied(port);
}
