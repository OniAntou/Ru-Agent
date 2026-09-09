namespace RuBackend.Runtime;

internal static class RuntimeEndpointRelay
{
    private static readonly HashSet<string> AllowedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type",
        "X-Request-Id",
        "X-Correlation-Id",
        "X-Worker-Version",
        "X-Runtime-Version",
        "X-Capabilities",
        "X-Error-Code",
        "X-Error-Detail"
    };

    private static readonly HashSet<string> ForbiddenResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length",
        "Transfer-Encoding",
        "Connection",
        "Set-Cookie",
        "Cookie",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Upgrade"
    };

    internal static async Task<byte[]?> ReadBoundedBodyAsync(
        HttpRequest request,
        long configuredMaximumBytes,
        CancellationToken cancellationToken)
    {
        var maximumBytes = Math.Max(0, configuredMaximumBytes);
        if (request.ContentLength is > 0 && request.ContentLength > maximumBytes)
        {
            return null;
        }

        await using var body = new MemoryStream((int)Math.Min(maximumBytes, 81920));
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (body.Length > maximumBytes - read)
            {
                return null;
            }

            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return body.ToArray();
    }

    public static async Task SendAsync(
        HttpContext context,
        IRuntimeWorkerClient workerClient,
        ILoggerFactory loggerFactory,
        string component,
        string unavailableMessage,
        long maximumBodyBytes,
        CancellationToken cancellationToken)
    {
        var requestBody = await ReadBoundedBodyAsync(
            context.Request,
            maximumBodyBytes,
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
                .CreateLogger(component)
                .LogWarning(
                    "Private runtime worker unavailable (component={Component}, exception_type={ExceptionType}).",
                    component,
                    exception.GetType().Name);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(
                new { status = "error", message = unavailableMessage },
                cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task WriteWorkerResponseAsync(
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
                var key = header.Key;
                if (ForbiddenResponseHeaders.Contains(key))
                {
                    continue;
                }
                if (AllowedResponseHeaders.Contains(key) || key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers[key] = header.Value;
                }
            }
        }

        context.Response.ContentLength = response.Body.Length;
        await context.Response.Body.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
    }
}
