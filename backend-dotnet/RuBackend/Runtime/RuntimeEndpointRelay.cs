namespace RuBackend.Runtime;

internal static class RuntimeEndpointRelay
{
    public static async Task SendAsync(
        HttpContext context,
        IRuntimeWorkerClient workerClient,
        ILoggerFactory loggerFactory,
        string component,
        string unavailableMessage,
        CancellationToken cancellationToken)
    {
        byte[] requestBody;
        await using (var body = new MemoryStream())
        {
            await context.Request.Body.CopyToAsync(body, cancellationToken).ConfigureAwait(false);
            requestBody = body.ToArray();
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
            await WriteResponseAsync(context, response, cancellationToken).ConfigureAwait(false);
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

    private static async Task WriteResponseAsync(
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
