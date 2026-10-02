using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Client;

/// <summary>
/// What the active model says about reasoning effort, plus the identity a settings write
/// has to echo back so the host applies the save to the snapshot the operator saw.
/// </summary>
public sealed record ModelReasoningSnapshot(
    string Identity,
    string? SelectedModel,
    string SelectedEffort,
    string State,
    IReadOnlyList<string> Options);

/// <summary>
/// Catalog details for one model. Rates are US dollars per million tokens, as published;
/// every field is nullable because "unpublished" and "zero" are different statements.
/// </summary>
public sealed record CatalogModelDetails(
    int? ContextLength,
    double? InputCost,
    double? OutputCost,
    double? CacheReadCost,
    double? CacheWriteCost);

/// <summary>
/// Authenticated settings API with bounded reads and envelope validation.
/// POST /settings returns {status: success|error}, GET /settings returns {status: ok}.
/// </summary>
public sealed class SettingsApiClient
{
    private const int MaximumModelsDevProviders = 1000;
    private const int MaximumReasoningOptions = 16;

    private readonly HttpClient _httpClient;
    private readonly PublicHostSession _session;

    public SettingsApiClient(PublicHostSession session, HttpClient? httpClient = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _httpClient = httpClient ?? new HttpClient();
        // The bearer is attached per request; a default header on a shared client
        // would leak one session's token onto another's calls.
    }

    public async Task<JsonElement> GetAsync(CancellationToken cancellationToken = default)
    {
        var url = new Uri(_session.BaseUri, "settings");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected.", (int)response.StatusCode);

        var content = await BoundedHttpResponseReader.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new AgentApiException("settings_read_failed", $"HTTP {(int)response.StatusCode}", (int)response.StatusCode);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(content); }
        catch (JsonException ex) { throw new AgentApiException("invalid_response", ex.Message); }
        using (doc)
        {
            var root = doc.RootElement.Clone();
            EnsureOkEnvelope(root, "settings_read_failed");
            return root;
        }
    }

    public async Task UpdateAsync(JsonObject body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var url = new Uri(_session.BaseUri, "settings");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected.", (int)response.StatusCode);

        var content = await BoundedHttpResponseReader.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new AgentApiException(
                ExtractErrorCode(content) ?? "settings_update_failed",
                $"HTTP {(int)response.StatusCode}",
                (int)response.StatusCode);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(content); }
        catch (JsonException ex) { throw new AgentApiException("invalid_response", ex.Message); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("status", out var statusProp)
                || statusProp.ValueKind != JsonValueKind.String)
            {
                throw new AgentApiException("invalid_response", "Missing settings status.");
            }

            var status = statusProp.GetString();
            if (!string.Equals(status, "ok", StringComparison.Ordinal)
                && !string.Equals(status, "success", StringComparison.Ordinal))
            {
                throw new AgentApiException(
                    ExtractErrorCode(content) ?? "settings_update_failed",
                    ExtractMessage(content) ?? $"Settings update rejected (status={status ?? "null"}).");
            }
        }
    }

    public async Task<IReadOnlyList<string>> GetModelsDevKeysAsync(CancellationToken cancellationToken = default)
    {
        var url = new Uri(_session.BaseUri, "settings/models-dev-keys");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected.", (int)response.StatusCode);

        var content = await BoundedHttpResponseReader.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new AgentApiException("settings_read_failed", $"HTTP {(int)response.StatusCode}", (int)response.StatusCode);

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("status", out var statusProp)
                || !string.Equals(statusProp.GetString(), "ok", StringComparison.Ordinal)
                || !root.TryGetProperty("provider_ids", out var ids)
                || ids.ValueKind != JsonValueKind.Array)
            {
                throw new AgentApiException("invalid_response", "Missing models-dev key list.");
            }

            var result = new List<string>();
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    result.Add(id.GetString()!);
                    if (result.Count >= 5000)
                    {
                        break;
                    }
                }
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new AgentApiException("invalid_response", ex.Message);
        }
    }

    /// <summary>
    /// Every provider published by models.dev (<c>GET /models-dev</c>). The id is what a
    /// later settings write puts in <c>backend_choice</c>, so only ids the host already
    /// accepted are kept; a malformed entry is dropped rather than failing the list.
    /// </summary>
    public async Task<IReadOnlyList<(string Id, string Name)>> GetModelsDevAsync(CancellationToken cancellationToken = default)
    {
        var url = new Uri(_session.BaseUri, "models-dev");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected.", (int)response.StatusCode);

        var content = await BoundedHttpResponseReader.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new AgentApiException("models_dev_read_failed", $"HTTP {(int)response.StatusCode}", (int)response.StatusCode);

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("status", out var statusProp)
                || !string.Equals(statusProp.GetString(), "ok", StringComparison.Ordinal)
                || !root.TryGetProperty("providers", out var providers)
                || providers.ValueKind != JsonValueKind.Array)
            {
                throw new AgentApiException("invalid_response", "Missing models-dev provider list.");
            }

            var result = new List<(string Id, string Name)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in providers.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("id", out var idProp)
                    || idProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var id = idProp.GetString()!.Trim();
                if (id.Length is 0 or > 64 || id.Any(char.IsControl) || !seen.Add(id))
                {
                    continue;
                }

                var name = entry.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                    ? nameProp.GetString()!.Trim()
                    : string.Empty;
                result.Add((id, name.Length is 0 or > 128 ? id : name));
                if (result.Count >= MaximumModelsDevProviders)
                {
                    break;
                }
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new AgentApiException("invalid_response", ex.Message);
        }
    }

    /// <summary>
    /// Live model catalog for one backend (<c>GET /models?backend=</c>).
    /// Returns id/support pairs, bounded and tolerant: an error envelope or an
    /// unparsable body throws so callers fall back to configured models.
    /// </summary>
    public async Task<IReadOnlyList<(string Id, bool Supported)>> GetModelsAsync(string? backend, CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(backend)
            ? new Uri(_session.BaseUri, "models")
            : new Uri(_session.BaseUri, "models?backend=" + Uri.EscapeDataString(backend.Trim()));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected.", (int)response.StatusCode);

        var content = await BoundedHttpResponseReader.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new AgentApiException("catalog_http_error", $"HTTP {(int)response.StatusCode}", (int)response.StatusCode);

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("catalog", out var catalog)
                || catalog.ValueKind != JsonValueKind.Array)
            {
                throw new AgentApiException("catalog_malformed", "Missing model catalog.");
            }

            var result = new List<(string Id, bool Supported)>();
            foreach (var entry in catalog.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("id", out var idProp)
                    || idProp.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(idProp.GetString()))
                {
                    continue;
                }

                var supported = entry.TryGetProperty("supported", out var supportedProp)
                    && supportedProp.ValueKind == JsonValueKind.True;
                result.Add((idProp.GetString()!, supported));
                if (result.Count >= 500)
                {
                    break;
                }
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new AgentApiException("catalog_malformed", ex.Message);
        }
    }

    /// <summary>
    /// Catalog details for one model: the published context window and rates, when the
    /// provider declared them. Returns null when the model is absent from the catalog;
    /// unpublished fields stay null instead of being invented.
    /// </summary>
    public async Task<CatalogModelDetails?> GetModelDetailsAsync(
        string? backend, string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var url = string.IsNullOrWhiteSpace(backend)
            ? new Uri(_session.BaseUri, "models")
            : new Uri(_session.BaseUri, "models?backend=" + Uri.EscapeDataString(backend.Trim()));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected.", (int)response.StatusCode);

        var content = await BoundedHttpResponseReader.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new AgentApiException("catalog_http_error", $"HTTP {(int)response.StatusCode}", (int)response.StatusCode);

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("catalog", out var catalog)
                || catalog.ValueKind != JsonValueKind.Array)
            {
                throw new AgentApiException("catalog_malformed", "Missing model catalog.");
            }

            foreach (var entry in catalog.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("id", out var idProp)
                    || idProp.ValueKind != JsonValueKind.String
                    || !string.Equals(idProp.GetString(), modelId, StringComparison.Ordinal))
                {
                    continue;
                }

                int? contextLength = null;
                if (entry.TryGetProperty("context_length", out var contextProp)
                    && contextProp.ValueKind == JsonValueKind.Number
                    && contextProp.TryGetInt32(out var parsedContext))
                {
                    contextLength = parsedContext;
                }

                double? input = null;
                double? output = null;
                double? cacheRead = null;
                double? cacheWrite = null;
                if (entry.TryGetProperty("cost", out var costProp) && costProp.ValueKind == JsonValueKind.Object)
                {
                    input = ReadRate(costProp, "input");
                    output = ReadRate(costProp, "output");
                    cacheRead = ReadRate(costProp, "cache_read");
                    cacheWrite = ReadRate(costProp, "cache_write");
                }

                return new CatalogModelDetails(contextLength, input, output, cacheRead, cacheWrite);
            }

            return null;
        }
        catch (JsonException ex)
        {
            throw new AgentApiException("catalog_malformed", ex.Message);
        }
    }

    private static double? ReadRate(JsonElement cost, string name) =>
        cost.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var rate)
        && double.IsFinite(rate)
        && rate >= 0
            ? rate
            : null;

    /// <summary>
    /// The reasoning surface of the active model: the snapshot identity a save must echo
    /// back, the model the effort belongs to, and the levels the catalog actually offers.
    /// The selected model is looked up in <c>catalog</c> then <c>models</c> because the
    /// host has been observed listing it in only one of the two.
    /// </summary>
    public async Task<ModelReasoningSnapshot> GetReasoningAsync(string? backend, CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(backend)
            ? new Uri(_session.BaseUri, "models")
            : new Uri(_session.BaseUri, "models?backend=" + Uri.EscapeDataString(backend.Trim()));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected.", (int)response.StatusCode);

        var content = await BoundedHttpResponseReader.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new AgentApiException("catalog_http_error", $"HTTP {(int)response.StatusCode}", (int)response.StatusCode);

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new AgentApiException("catalog_malformed", "Missing model catalog.");
            }

            var identity = ReadString(root, "identity") ?? string.Empty;
            var selectedModel = ReadString(root, "selected_model");
            var selectedEffort = ReadString(root, "selected_effort") ?? "default";
            var state = "unknown";
            var options = Array.Empty<string>();

            foreach (var arrayName in new[] { "catalog", "models" })
            {
                if (!root.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var entry in array.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object
                        || !string.Equals(ReadString(entry, "id"), selectedModel, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (entry.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.Object)
                    {
                        state = ReadString(reasoning, "state") ?? "unknown";
                        if (reasoning.TryGetProperty("options", out var levels) && levels.ValueKind == JsonValueKind.Array)
                        {
                            var parsed = new List<string>();
                            foreach (var level in levels.EnumerateArray())
                            {
                                if (level.ValueKind == JsonValueKind.String
                                    && !string.IsNullOrWhiteSpace(level.GetString())
                                    && level.GetString()!.Length <= 32)
                                {
                                    parsed.Add(level.GetString()!);
                                }

                                if (parsed.Count >= MaximumReasoningOptions)
                                {
                                    break;
                                }
                            }

                            options = [.. parsed];
                        }
                    }

                    break;
                }

                if (options.Length > 0 || state is not "unknown")
                {
                    break;
                }
            }

            return new ModelReasoningSnapshot(identity, selectedModel, selectedEffort, state, options);
        }
        catch (JsonException ex)
        {
            throw new AgentApiException("catalog_malformed", ex.Message);
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public async Task<string> RemoveProviderAsync(string backendChoice, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendChoice);
        var url = new Uri(_session.BaseUri, "settings/provider/remove");
        var body = new JsonObject { ["backend_choice"] = backendChoice }.ToJsonString();
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected.", (int)response.StatusCode);

        var content = await BoundedHttpResponseReader.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new AgentApiException("provider_remove_failed", $"HTTP {(int)response.StatusCode}", (int)response.StatusCode);

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            var message = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("message", out var msg)
                && msg.ValueKind == JsonValueKind.String
                ? msg.GetString() ?? string.Empty
                : string.Empty;
            var status = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("status", out var statusProp)
                && statusProp.ValueKind == JsonValueKind.String
                ? statusProp.GetString()
                : null;
            if (!string.Equals(status, "success", StringComparison.Ordinal))
            {
                throw new AgentApiException(
                    "provider_remove_failed",
                    string.IsNullOrWhiteSpace(message) ? "Provider could not be removed." : message);
            }

            return string.IsNullOrWhiteSpace(message) ? "Signed out." : message;
        }
        catch (JsonException ex)
        {
            throw new AgentApiException("invalid_response", ex.Message);
        }
    }

    private static void EnsureOkEnvelope(JsonElement root, string operation)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("status", out var statusProp)
            || statusProp.ValueKind != JsonValueKind.String
            || !string.Equals(statusProp.GetString(), "ok", StringComparison.Ordinal))
        {
            throw new AgentApiException(operation, "Settings response missing ok envelope.");
        }
    }

    private static string? ExtractErrorCode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error_code", out var code) && code.ValueKind == JsonValueKind.String)
                return code.GetString();
        }
        catch { }
        return null;
    }

    private static string? ExtractMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                return msg.GetString();
        }
        catch { }
        return null;
    }
}
