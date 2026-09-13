using System.Text;
using System.Text.Json.Nodes;

namespace RuAgent.Runtime.Adapters.Runtime;

/// <summary>
/// Serves the read-only runtime status surface from the worker's typed settings.
/// Model credentials intentionally stay in the public host, so the worker
/// reports the active configured model instead of attempting a credentialed
/// provider catalog request.
/// </summary>
public sealed class ManagedRuntimeStatusRequestHandler : IPrivateRuntimeRequestHandler
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlyDictionary<string, string> BackendAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["opencode"] = "OpenCode Zen",
            ["kilo"] = "Kilo Gateway",
            ["custom"] = "Custom OpenAI",
        };
    private static readonly IReadOnlySet<string> SupportedBackends =
        new HashSet<string>(
            ["OpenCode Zen", "Kilo Gateway", "Custom OpenAI"],
            StringComparer.Ordinal);
    private readonly ManagedRuntimeSettingsService _settings;

    public ManagedRuntimeStatusRequestHandler(ManagedRuntimeSettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public Task<PrivateRuntimeResponse> InvokeAsync(
        PrivateRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.PathAndQuery.Split('?', 2)[0];
        return (request.Method, path) switch
        {
            ("GET", "/status") => Task.FromResult(StatusResponse(_settings.Current)),
            ("GET", "/models") => Task.FromResult(ModelsResponse(request.PathAndQuery, _settings.Current)),
            _ => throw new PrivateRuntimeRequestException("unsupported_route"),
        };
    }

    private static PrivateRuntimeResponse StatusResponse(
        RuAgent.Runtime.Core.Contracts.RuntimeSettingsSnapshot settings)
    {
        return JsonResponse(
            200,
            new JsonObject
            {
                ["status"] = "ok",
                ["message"] = "Backend is running and ready for the WinUI3 UI.",
                ["backend"] = settings.Backend,
            });
    }

    private static PrivateRuntimeResponse ModelsResponse(
        string pathAndQuery,
        RuAgent.Runtime.Core.Contracts.RuntimeSettingsSnapshot settings)
    {
        var requestedBackend = QueryValue(pathAndQuery, "backend");
        if (requestedBackend is not null)
        {
            requestedBackend = BackendAliases.TryGetValue(requestedBackend, out var alias)
                ? alias
                : requestedBackend;
            if (!SupportedBackends.Contains(requestedBackend))
            {
                return JsonResponse(
                    200,
                    new JsonObject
                    {
                        ["status"] = "error",
                        ["message"] = "Unsupported model backend.",
                    });
            }

            if (!string.Equals(requestedBackend, settings.Backend, StringComparison.Ordinal))
            {
                return JsonResponse(
                    200,
                    new JsonObject
                    {
                        ["status"] = "error",
                        ["message"] = "The requested model backend is not active.",
                    });
            }
        }

        return JsonResponse(
            200,
            new JsonObject
            {
                ["status"] = "ok",
                ["models"] = new JsonArray(settings.ModelName),
            });
    }

    private static string? QueryValue(string pathAndQuery, string key)
    {
        var separator = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        if (separator < 0 || separator == pathAndQuery.Length - 1)
        {
            return null;
        }

        foreach (var pair in pathAndQuery[(separator + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var rawKey = equals < 0 ? pair : pair[..equals];
            if (!string.Equals(Decode(rawKey), key, StringComparison.Ordinal))
            {
                continue;
            }

            return equals < 0 ? string.Empty : Decode(pair[(equals + 1)..]);
        }

        return null;
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return string.Empty;
        }
    }

    private static PrivateRuntimeResponse JsonResponse(int statusCode, JsonObject payload) =>
        new(statusCode, "application/json", StrictUtf8.GetBytes(payload.ToJsonString()));
}
