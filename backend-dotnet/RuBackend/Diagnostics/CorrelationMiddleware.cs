namespace RuBackend.Diagnostics;

using Microsoft.AspNetCore.Http;

public sealed class CorrelationMiddleware
{
    private const string HeaderName = "X-Correlation-Id";
    private readonly RequestDelegate _next;

    public CorrelationMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string? correlationId = null;
        if (context.Request.Headers.TryGetValue(HeaderName, out var values) && values.Count > 0)
        {
            correlationId = values[0];
        }
        else if (context.Request.Query.TryGetValue("correlation_id", out var queryValues) && queryValues.Count > 0)
        {
            correlationId = queryValues[0];
        }

        var sanitized = CorrelationContext.Sanitize(correlationId);
        context.Response.Headers[HeaderName] = sanitized;

        using (CorrelationContext.Push(sanitized))
        {
            await _next(context).ConfigureAwait(false);
        }
    }
}
