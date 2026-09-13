using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using RuAgent.Runtime.Adapters.Bootstrap;

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
            throw new ArgumentException("Worker binds only to 127.0.0.1 and valid ports.");
        }

        var token = Environment.GetEnvironmentVariable("RU_AGENT_WORKER_TOKEN")
            ?? throw new InvalidOperationException("RU_AGENT_WORKER_TOKEN is required.");
        var bootstrapPath = Environment.GetEnvironmentVariable("RU_AGENT_PROVIDER_BOOTSTRAP")
            ?? throw new InvalidOperationException("RU_AGENT_PROVIDER_BOOTSTRAP is required.");
        var sessionRoot = Path.GetDirectoryName(Path.GetFullPath(bootstrapPath))
            ?? throw new InvalidOperationException("Provider bootstrap root is invalid.");
        var providerData = ProtectedProviderBootstrap.ReadAndDelete(sessionRoot, bootstrapPath);
        Environment.SetEnvironmentVariable("RU_AGENT_PROVIDER_BOOTSTRAP", null);
        var projectRoot = Environment.GetEnvironmentVariable("RU_PROJECT_ROOT")
            ?? Directory.GetCurrentDirectory();
        var composition = ManagedWorkerComposition.Create(
            new ManagedWorkerCompositionOptions(
                port,
                token,
                providerData,
                projectRoot,
                sessionRoot));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://{host}:{port}");
        var app = builder.Build();
        app.UseWebSockets();
        try
        {
            app.Run(composition.AgentServer.HandleAsync);
            await app.RunAsync();
        }
        finally
        {
            await composition.DisposeAsync().ConfigureAwait(false);
        }
    }
}
