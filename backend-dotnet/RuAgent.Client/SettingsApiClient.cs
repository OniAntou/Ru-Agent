using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Client;

/// <summary>
/// Authenticated settings API with bounded reads and envelope validation.
/// POST /settings returns {status: success|error}, GET /settings returns {status: ok}.
/// </summary>
public sealed class SettingsApiClient
{
    private readonly HttpClient _httpClient;
    private readonly PublicHostSession _session;

    public SettingsApiClient(PublicHostSession session, HttpClient? httpClient = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _httpClient = httpClient ?? new HttpClient();
        if (_httpClient.DefaultRequestHeaders.Authorization is null)
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.BearerToken);
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
