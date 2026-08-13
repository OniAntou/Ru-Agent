namespace RuBackend.Runtime;

public static class StatusEndpoints
{
    public static void Map(WebApplication app)
    {
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
