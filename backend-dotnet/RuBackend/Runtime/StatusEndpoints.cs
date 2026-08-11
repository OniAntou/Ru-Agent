using Microsoft.Extensions.Options;
using RuBackend.Configuration;

namespace RuBackend.Runtime;

public static class StatusEndpoints
{
    public static void Map(WebApplication app)
    {
        var ownership = app.Services
            .GetRequiredService<IOptions<BackendOptions>>()
            .Value
            .Ownership
            .Status;
        if (!string.Equals(ownership?.Trim(), "aspnet", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        app.MapGet("/status", HandleAsync);
        app.MapGet("/models", HandleAsync);
    }

    private static Task HandleAsync(
        HttpContext context,
        IRuntimeWorkerClient workerClient,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        RuntimeEndpointRelay.SendAsync(
            context,
            workerClient,
            loggerFactory,
            "RuBackend.Status",
            "Runtime worker is not ready.",
            cancellationToken);
}
