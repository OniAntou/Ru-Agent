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
