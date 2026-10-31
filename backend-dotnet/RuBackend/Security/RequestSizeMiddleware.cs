using System.Buffers;
using Microsoft.Extensions.Options;
using RuBackend.Configuration;

namespace RuBackend.Security;

public sealed class RequestSizeMiddleware
{
    private const int BufferSize = 64 * 1024;
    private readonly RequestDelegate _next;
    private readonly IOptions<BackendOptions> _options;

    public RequestSizeMiddleware(RequestDelegate next, IOptions<BackendOptions> options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var maxBytes = _options.Value.MaxRequestBodyBytes;
        // Both create routes carry the same optional provider/image payload and share
        // one endpoint reader; a path-only check here used to cap /runs/selected at the
        // general 8 MiB limit while /runs was allowed the larger bound.
        if (context.Request.Path.Equals("/agent/v1/runs", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.Equals("/agent/v1/runs/selected", StringComparison.OrdinalIgnoreCase))
        {
            maxBytes = Math.Max(maxBytes, _options.Value.AgentRunMaxRequestBodyBytes);
        }
        else if (context.Request.Path.Equals("/sessions/import", StringComparison.OrdinalIgnoreCase))
        {
            // Above the endpoint's own bound so an oversized import is answered by the
            // interchange endpoint as import_too_large instead of as a transport rejection.
            maxBytes = Math.Max(maxBytes, _options.Value.SessionImportMaxRequestBodyBytes);
        }

        if (maxBytes < 0)
        {
            maxBytes = 0;
        }

        if (context.Request.ContentLength is long declaredLength && declaredLength > maxBytes)
        {
            await RejectAsync(context);
            return;
        }

        // A declared Content-Length is enforced by the server's frame handling,
        // so only bodies without one (chunked) need the measured pass. This
        // avoids buffering and reading every request body twice.
        if (context.Request.ContentLength is null
            && (HttpMethods.IsPost(context.Request.Method)
                || HttpMethods.IsPut(context.Request.Method)
                || HttpMethods.IsPatch(context.Request.Method)))
        {
            context.Request.EnableBuffering();
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                long totalBytes = 0;
                int bytesRead;
                while ((bytesRead = await context.Request.Body.ReadAsync(buffer.AsMemory(0, BufferSize), context.RequestAborted)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > maxBytes)
                    {
                        await RejectAsync(context);
                        return;
                    }
                }

                context.Request.Body.Position = 0;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        await _next(context);
    }

    private static async Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await context.Response.WriteAsJsonAsync(new { detail = "Request body too large" });
    }
}
