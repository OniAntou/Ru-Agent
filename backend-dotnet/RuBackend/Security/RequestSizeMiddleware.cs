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
        // general 8 MiB limit while /runs was allowed the larger bound. A steer carries
        // what a run's prompt can (ADR 0056), so its route shares that bound too.
        if (IsAgentRunCreatePath(context.Request.Path) || IsRunSteerPath(context.Request.Path))
        {
            maxBytes = Math.Max(maxBytes, _options.Value.AgentRunMaxRequestBodyBytes);
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

    /// <summary>
    /// Routing ignores trailing slashes, so "/agent/v1/runs/" (and a repeated-slash
    /// variant) still reaches the same create endpoint as "/agent/v1/runs" and must
    /// inherit its larger body bound. The match stays segment-exact so a lookalike
    /// path such as "/agent/v1/runsXYZ" never widens its cap.
    /// </summary>
    private static bool IsAgentRunCreatePath(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        value = value.TrimEnd('/');
        return value.Equals("/agent/v1/runs", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/agent/v1/runs/selected", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "/agent/v1/runs/{runId}/steers", segment-exact like the create routes: one
    /// non-empty run segment, so a deeper or lookalike path keeps the generic cap.
    /// The endpoint validates the run id itself.
    /// </summary>
    private static bool IsRunSteerPath(PathString path)
    {
        const string prefix = "/agent/v1/runs/";
        const string suffix = "/steers";
        var value = path.Value?.TrimEnd('/');
        if (value is null
            || value.Length <= prefix.Length + suffix.Length
            || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var runSegment = value.AsSpan(prefix.Length, value.Length - prefix.Length - suffix.Length);
        return runSegment.IndexOf('/') < 0;
    }

    private static async Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await context.Response.WriteAsJsonAsync(new { detail = "Request body too large" });
    }
}
