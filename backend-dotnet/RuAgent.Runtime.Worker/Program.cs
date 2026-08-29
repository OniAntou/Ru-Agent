using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;

namespace RuAgent.Runtime.Worker;

internal static class WorkerEntryPoint
{
    public static async Task Main(string[] args)
    {
        var host = "127.0.0.1";
        var port = 0;
        var devicePort = 0;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--host" when index + 1 < args.Length:
                    host = args[++index];
                    break;
                case "--port" when index + 1 < args.Length && int.TryParse(args[++index], out var parsedPort):
                    port = parsedPort;
                    break;
                case "--device-port" when index + 1 < args.Length && int.TryParse(args[++index], out var parsedDevicePort):
                    devicePort = parsedDevicePort;
                    break;
                default:
                    throw new ArgumentException("Only --host, --port, and --device-port are supported.");
            }
        }

        if (devicePort == 0)
        {
            devicePort = port;
        }

        if (host != "127.0.0.1"
            || port is < 1 or > 65_535
            || devicePort is < 1 or > 65_535)
        {
            throw new ArgumentException("Worker binds only to 127.0.0.1 and valid ports.");
        }

        var token = Environment.GetEnvironmentVariable("RU_AGENT_WORKER_TOKEN")
            ?? throw new InvalidOperationException("RU_AGENT_WORKER_TOKEN is required.");
        var deviceToken = Environment.GetEnvironmentVariable("RU_DEVICE_WORKER_TOKEN")
            ?? throw new InvalidOperationException("RU_DEVICE_WORKER_TOKEN is required.");
        var bootstrapPath = Environment.GetEnvironmentVariable("RU_AGENT_PROVIDER_BOOTSTRAP")
            ?? throw new InvalidOperationException("RU_AGENT_PROVIDER_BOOTSTRAP is required.");
        var sessionRoot = Path.GetDirectoryName(Path.GetFullPath(bootstrapPath))
            ?? throw new InvalidOperationException("Provider bootstrap root is invalid.");
        var providerData = ProtectedProviderBootstrap.ReadAndDelete(sessionRoot, bootstrapPath);
        // The bootstrap is an agent-only handoff. Remove the path from the
        // process environment before composing the device v1 graph so a v1
        // adapter cannot rediscover the provider credential channel.
        Environment.SetEnvironmentVariable("RU_AGENT_PROVIDER_BOOTSTRAP", null);
        var projectRoot = Environment.GetEnvironmentVariable("RU_PROJECT_ROOT")
            ?? Directory.GetCurrentDirectory();
        var logRoot = Environment.GetEnvironmentVariable("RU_LOG_ROOT");
        var composition = ManagedWorkerComposition.Create(
            new ManagedWorkerCompositionOptions(
                port,
                devicePort,
                token,
                deviceToken,
                providerData,
                projectRoot)
            {
                LogRoot = logRoot,
            });

        var builder = WebApplication.CreateBuilder();
        var urls = devicePort == port
            ? new[] { $"http://{host}:{port}" }
            : new[] { $"http://{host}:{port}", $"http://{host}:{devicePort}" };
        builder.WebHost.UseUrls(urls);
        var app = builder.Build();
        app.UseWebSockets();
        try
        {
            app.Run(context => string.Equals(context.Request.Path, "/worker/v1", StringComparison.Ordinal)
                ? composition.DeviceServer.HandleAsync(context)
                : composition.AgentServer.HandleAsync(context));
            await app.RunAsync();
        }
        finally
        {
            await composition.DisposeAsync().ConfigureAwait(false);
        }
    }
}
