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
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await _next(context);
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        var candidate = authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[BearerPrefix.Length..].Trim()
            : string.Empty;

        if (candidate.Length == 0 && context.WebSockets.IsWebSocketRequest)
        {
            candidate = context.Request.Query["token"].ToString().Trim();
        }

        var configuredToken = _options.Value.DesktopToken?.Trim() ?? string.Empty;
        if (!TokensMatch(candidate, configuredToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { detail = "Unauthorized" });
            return;
        }

        await _next(context);
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
