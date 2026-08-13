namespace RuBackend.Runtime;

public static class VoiceEndpoints
{
    private static readonly (string Method, string Path)[] Routes =
    [
        (HttpMethods.Get, "/voice/status"),
        (HttpMethods.Post, "/voice/latency"),
        (HttpMethods.Post, "/voice/tts"),
        (HttpMethods.Post, "/voice/warmup"),
        (HttpMethods.Post, "/voice/release"),
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
            "RuBackend.Voice",
            "Voice runtime is temporarily unavailable.",
            cancellationToken);
}
