using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RuBackend.Configuration;

namespace RuBackend.Security;

public sealed class DesktopTokenMiddleware
{
    private const string BearerPrefix = "Bearer ";
    private readonly RequestDelegate _next;
    private readonly IOptions<BackendOptions> _options;

    public DesktopTokenMiddleware(RequestDelegate next, IOptions<BackendOptions> options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsOptions(context.Request.Method)
            && !IsSensitiveRoute(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        var candidate = authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[BearerPrefix.Length..].Trim()
            : string.Empty;

        // Bearer travels via the Authorization header only. Query-string tokens
        // are rejected so secrets never land in access logs or ETW traces.

        var configuredToken = _options.Value.DesktopToken?.Trim() ?? string.Empty;
        if (!TokensMatch(candidate, configuredToken))
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await socket.CloseAsync(
                    (System.Net.WebSockets.WebSocketCloseStatus)4401,
                    "Unauthorized",
                    CancellationToken.None);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { detail = "Unauthorized" });
            return;
        }

        await _next(context);
    }

    private static bool IsSensitiveRoute(PathString path)
    {
        return path.StartsWithSegments("/settings", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/sessions", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/agent", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/host", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/diagnostics", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TokensMatch(string candidate, string configuredToken)
    {
        if (candidate.Length == 0 || configuredToken.Length == 0)
        {
            return false;
        }

        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredToken));
        return CryptographicOperations.FixedTimeEquals(candidateHash, configuredHash);
    }
}
