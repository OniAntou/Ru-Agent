using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RuAgent.Client;

public sealed class SessionApiClient
{
    private readonly HttpClient _httpClient;
    private readonly PublicHostSession _session;

    public SessionApiClient(PublicHostSession session, HttpClient? httpClient = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _httpClient = httpClient ?? new HttpClient();
        // The bearer is attached per request; a default header on a shared client
        // would leak one session's token onto another's calls.
    }

    public async Task<string> CreateAsync(CancellationToken cancellationToken = default)
    {
        var url = new Uri(_session.BaseUri, "sessions/new");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected.", (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
            throw new AgentApiException("session_create_failed", $"HTTP {(int)response.StatusCode}", (int)response.StatusCode);

        var content = await BoundedHttpResponseReader.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return ParseSessionId(content);
    }

    public static string ParseSessionId(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new AgentApiException("invalid_response", "Empty response");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new AgentApiException("invalid_response", ex.Message); }
        using (doc)
        {

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new AgentApiException("invalid_response", "Root must be object");

        // Strict: allow only status, session_id, session (and maybe id?) but reject unknown
        var allowed = new HashSet<string>(["status", "session_id", "session", "id", "message"], StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
        {
            if (!allowed.Contains(prop.Name))
                throw new AgentApiException("unknown_field", $"Unexpected field {prop.Name}");
        }

        if (!root.TryGetProperty("status", out var statusProp) || statusProp.ValueKind != JsonValueKind.String)
            throw new AgentApiException("invalid_response", "Missing status");
        var status = statusProp.GetString();
        if (!string.Equals(status, "ok", StringComparison.Ordinal))
            throw new AgentApiException("invalid_response", $"Status must be ok, got {status}");

        string? sessionId = null;
        if (root.TryGetProperty("session_id", out var sidProp) && sidProp.ValueKind == JsonValueKind.String)
            sessionId = sidProp.GetString();
        else if (root.TryGetProperty("session", out var sessProp) && sessProp.ValueKind == JsonValueKind.Object && sessProp.TryGetProperty("id", out var innerId) && innerId.ValueKind == JsonValueKind.String)
            sessionId = innerId.GetString();
        else if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            sessionId = idProp.GetString();

        if (string.IsNullOrWhiteSpace(sessionId))
            throw new AgentApiException("invalid_session_id", "Missing session_id");

        // Bounded validation: 1..64 chars, pattern a-zA-Z0-9_- 
        if (sessionId.Length > 64 || sessionId.Length < 1)
            throw new AgentApiException("invalid_session_id", "Length invalid");
        foreach (var c in sessionId)
        {
            if (!((c is >= 'a' and <= 'z')
                || (c is >= 'A' and <= 'Z')
                || (c is >= '0' and <= '9')
                || c is '_' or '-'))
                throw new AgentApiException("invalid_session_id", $"Invalid char {c}");
        }

        return sessionId;
        }
    }

    public async Task<JsonElement> ListAsync(CancellationToken cancellationToken = default)
    {
        var url = new Uri(_session.BaseUri, "sessions");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected.", (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
            throw new AgentApiException("session_list_failed", $"HTTP {(int)response.StatusCode}", (int)response.StatusCode);

        var content = await BoundedHttpResponseReader.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(content);
        var listRoot = doc.RootElement.Clone();
        EnsureOkEnvelope(listRoot, "session_list_failed");
        return listRoot;
    }

    public async Task<JsonElement> RewindAsync(string sessionId, int toMessageIndex, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var url = new Uri(_session.BaseUri, $"sessions/{Uri.EscapeDataString(sessionId)}/rewind");
        var payload = JsonSerializer.Serialize(new
        {
            to_message_index = toMessageIndex,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected.", (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
            throw new AgentApiException("session_rewind_failed", $"HTTP {(int)response.StatusCode}", (int)response.StatusCode);

        var content = await BoundedHttpResponseReader.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(content);
        var rewindRoot = doc.RootElement.Clone();
        EnsureOkEnvelope(rewindRoot, "session_rewind_failed");
        return rewindRoot;
    }

    public async Task<JsonElement> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var url = new Uri(_session.BaseUri, $"sessions/{Uri.EscapeDataString(sessionId)}/activate");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected.", (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
            throw new AgentApiException("session_get_failed", $"HTTP {(int)response.StatusCode}", (int)response.StatusCode);

        var content = await BoundedHttpResponseReader.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement.Clone();
        EnsureOkEnvelope(root, "session_get_failed");
        return root;
    }

    private static void EnsureOkEnvelope(JsonElement root, string operation)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("status", out var statusProp)
            || statusProp.ValueKind != JsonValueKind.String
            || !string.Equals(statusProp.GetString(), "ok", StringComparison.Ordinal))
        {
            string? message = null;
            string? code = null;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                    message = msg.GetString();
                if (root.TryGetProperty("error_code", out var ec) && ec.ValueKind == JsonValueKind.String)
                    code = ec.GetString();
            }

            throw new AgentApiException(
                code ?? operation,
                message ?? $"Session operation rejected (status missing or not ok).");
        }
    }
}
