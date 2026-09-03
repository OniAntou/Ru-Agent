using RuBackend.Configuration;
using RuBackend.Runtime;

namespace RuBackend.Vts;

public static class VtsEndpoints
{
    private static readonly (string Method, string Path)[] Routes =
    [
        (HttpMethods.Get, "/vts/mouse-look"),
        (HttpMethods.Post, "/vts/mouse-look"),
        (HttpMethods.Get, "/vts/overlay"),
        (HttpMethods.Post, "/vts/overlay"),
        (HttpMethods.Get, "/vts/status"),
        (HttpMethods.Post, "/vts/connect"),
        (HttpMethods.Get, "/vts/config"),
        (HttpMethods.Post, "/vts/config"),
        (HttpMethods.Post, "/vts/activity"),
        (HttpMethods.Post, "/vts/action"),
        (HttpMethods.Post, "/vts/speaking"),
    ];

    public static void Map(WebApplication app)
    {
        foreach (var route in Routes)
        {
            app.MapMethods(route.Path, [route.Method], HandleAsync);
        }
    }

    private static async Task HandleAsync(
        HttpContext context,
        IRuntimeWorkerClient workerClient,
        ILoggerFactory loggerFactory,
        Microsoft.Extensions.Options.IOptions<BackendOptions> options,
        CancellationToken cancellationToken)
    {
        var requestBody = await RuntimeEndpointRelay.ReadBoundedBodyAsync(
            context.Request,
            options.Value.MaxRequestBodyBytes,
            cancellationToken).ConfigureAwait(false);
        if (requestBody is null)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(
                new { detail = "Request body too large" },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var pathAndQuery = context.Request.PathBase.ToUriComponent()
                + context.Request.Path.ToUriComponent()
                + context.Request.QueryString.Value;
            var response = await workerClient.SendAsync(
                new RuntimeWorkerRequest(
                    new HttpMethod(context.Request.Method),
                    pathAndQuery,
                    context.Request.ContentType,
                    requestBody),
                cancellationToken).ConfigureAwait(false);
            await WriteWorkerResponseAsync(context, response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            loggerFactory
                .CreateLogger("RuBackend.Vts")
                .LogWarning(
                    "VTS runtime worker unavailable (exception_type={ExceptionType}).",
                    exception.GetType().Name);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(
                new { status = "error", message = "VTube Studio runtime is temporarily unavailable." },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteWorkerResponseAsync(
        HttpContext context,
        RuntimeWorkerResponse response,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = response.StatusCode;
        if (!string.IsNullOrWhiteSpace(response.ContentType))
        {
            context.Response.ContentType = response.ContentType;
        }

        if (response.Headers is not null)
        {
            foreach (var header in response.Headers)
            {
                context.Response.Headers[header.Key] = header.Value;
            }
        }

        context.Response.ContentLength = response.Body.Length;
        await context.Response.Body.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
    }
}
