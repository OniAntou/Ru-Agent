namespace RuBackend.Runtime;

public static class ObservationEndpoints
{
    private static readonly (string Method, string Path)[] Routes =
    [
        (HttpMethods.Post, "/observation/preflight"),
        (HttpMethods.Get, "/observation/vision-config"),
        (HttpMethods.Post, "/observation/react"),
        (HttpMethods.Post, "/observation/cancel"),
        (HttpMethods.Post, "/observation/ack"),
        (HttpMethods.Post, "/observation/start"),
        (HttpMethods.Post, "/observation/stop"),
        (HttpMethods.Get, "/observation/status"),
    ];

    public static void Map(WebApplication app)
    {
        foreach (var route in Routes)
        {
            app.MapMethods(route.Path, [route.Method], HandleAsync);
        }
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
            "RuBackend.Observation",
            "Observation runtime is temporarily unavailable.",
            cancellationToken);
}
