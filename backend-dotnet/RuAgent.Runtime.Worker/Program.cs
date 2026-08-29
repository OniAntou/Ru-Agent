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
        var deviceToken = Environment.GetEnvironmentVariable("RU_DEVICE_WORKER_TOKEN") ?? token;
        var bootstrapPath = Environment.GetEnvironmentVariable("RU_AGENT_PROVIDER_BOOTSTRAP")
            ?? throw new InvalidOperationException("RU_AGENT_PROVIDER_BOOTSTRAP is required.");
        var sessionRoot = Path.GetDirectoryName(Path.GetFullPath(bootstrapPath))
            ?? throw new InvalidOperationException("Provider bootstrap root is invalid.");
        var providerData = ProtectedProviderBootstrap.ReadAndDelete(sessionRoot, bootstrapPath);
        var provider = new OpenAiCompatibleModelProvider(new HttpClient());
        var options = new WorkerV2Options(
            port,
            token,
            providerData.Endpoint,
            providerData.Model,
            providerData.Credential);
        var server = new WorkerV2Server(
            token,
            new WorkerV2StepService(provider, options));
        var deviceServer = new WorkerV1Server(
            deviceToken,
            new UnavailableWorkerV1RequestRouter());

        var builder = WebApplication.CreateBuilder();
        var urls = devicePort == port
            ? new[] { $"http://{host}:{port}" }
            : new[] { $"http://{host}:{port}", $"http://{host}:{devicePort}" };
        builder.WebHost.UseUrls(urls);
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(context => string.Equals(context.Request.Path, "/worker/v1", StringComparison.Ordinal)
            ? deviceServer.HandleAsync(context)
            : server.HandleAsync(context));
        await app.RunAsync();
    }
}
