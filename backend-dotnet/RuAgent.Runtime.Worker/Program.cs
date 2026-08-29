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
                default:
                    throw new ArgumentException("Only --host and --port are supported.");
            }
        }

        if (host != "127.0.0.1" || port is < 1 or > 65_535)
        {
            throw new ArgumentException("Worker binds only to 127.0.0.1 and a valid port.");
        }

        var token = Environment.GetEnvironmentVariable("RU_AGENT_WORKER_TOKEN")
            ?? throw new InvalidOperationException("RU_AGENT_WORKER_TOKEN is required.");
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

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://{host}:{port}");
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(server.HandleAsync);
        await app.RunAsync();
    }
}
