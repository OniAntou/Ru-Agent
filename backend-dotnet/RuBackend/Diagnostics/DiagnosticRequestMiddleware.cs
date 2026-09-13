using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace RuBackend.Diagnostics;

/// <summary>
/// Emits one bounded event at the public HTTP boundary without inspecting the
/// request body, query values, response body, or exception text.
/// </summary>
public sealed class DiagnosticRequestMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IDiagnosticEmitter _emitter;

    public DiagnosticRequestMiddleware(RequestDelegate next, IDiagnosticEmitter emitter)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsDiagnosticsPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        Exception? failure = null;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            var statusCode = context.Response.StatusCode;
            var failed = failure is not null || statusCode >= 400;
            _emitter.TryEmit(
                level: failed ? "warning" : "info",
                component: "host.http",
                eventCode: MapEventCode(context.Request.Path),
                durationMs: stopwatch.Elapsed.TotalMilliseconds,
                status: failed ? "failed" : "succeeded",
                errorCode: failure is not null
                    ? "http_exception"
                    : statusCode >= 400 ? $"http_{statusCode}" : null,
                exceptionKind: ClassifyException(failure));
        }
    }

    private static bool IsDiagnosticsPath(PathString path)
    {
        var value = path.Value;
        return value?.Equals("/diagnostics", StringComparison.OrdinalIgnoreCase) == true ||
               value?.StartsWith("/diagnostics/", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string MapEventCode(PathString path)
    {
        var value = path.Value ?? string.Empty;
        if (value.StartsWith("/settings", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("/sessions", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("/activity-log", StringComparison.OrdinalIgnoreCase))
        {
            return "http.business";
        }

        if (value.StartsWith("/diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            return "http.diagnostics";
        }

        if (value.Equals("/status", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/models", StringComparison.OrdinalIgnoreCase))
        {
            return "http.status";
        }

        return "http.request";
    }

    private static string? ClassifyException(Exception? exception)
    {
        return exception switch
        {
            null => null,
            OperationCanceledException => "cancelled",
            TimeoutException => "timeout",
            UnauthorizedAccessException => "unauthorized",
            HttpRequestException or System.Net.Sockets.SocketException => "network",
            IOException or FileNotFoundException or DirectoryNotFoundException => "io",
            _ => "unknown",
        };
    }
}
