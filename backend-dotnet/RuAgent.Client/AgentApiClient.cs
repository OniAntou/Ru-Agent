using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RuAgent.Protocol.Harness;

namespace RuAgent.Client;

public sealed class AgentApiClient : IAgentPolicyClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly PublicHostSession _session;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private bool _disposed;

    public AgentApiClient(PublicHostSession session, HttpClient? httpClient = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        if (httpClient is null)
        {
            _httpClient = new HttpClient();
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
        }

        if (_httpClient.DefaultRequestHeaders.Authorization is null)
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.BearerToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private void EnsureAuth(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
    }

    private void AddInteractiveHeader(HttpRequestMessage request, bool interactive)
    {
        if (interactive)
            request.Headers.Add("X-Interactive-Confirmed", "true");
    }

    public async Task<CreateAgentRunResponse> CreateRunAsync(CreateAgentRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var url = new Uri(_session.BaseUri, "agent/v1/runs");
        var json = JsonSerializer.Serialize(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        EnsureAuth(httpRequest);
        using var resp = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected", (int)resp.StatusCode);
        if ((int)resp.StatusCode == 409)
        {
            throw new AgentApiException(ExtractErrorCode(body) ?? "conflict", body, (int)resp.StatusCode);
        }
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(
                ExtractErrorCode(body) ?? "agent_create_failed",
                body,
                (int)resp.StatusCode);

        return ParseCreateResponseStrict(body);
    }

    public async Task<GetAgentRunResponse> GetRunAsync(string runId, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected", (int)resp.StatusCode);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "get_run_failed", body, (int)resp.StatusCode);
        return ParseGetResponseStrict(body);
    }
    public async Task<AgentRunTrustResponse> TrustAsync(
        string runId,
        TrustAgentRunRequest request,
        CancellationToken ct = default)
    {
        ValidateRunId(runId);
        ArgumentNullException.ThrowIfNull(request);
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/trust");
        var json = JsonSerializer.Serialize(request);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected", (int)resp.StatusCode);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "trust_failed", body, (int)resp.StatusCode);

        try
        {
            var result = JsonSerializer.Deserialize<AgentRunTrustResponse>(body, JsonOptions);
            if (result is null
                || !string.Equals(result.RunId, runId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(result.Status))
            {
                throw new AgentApiException("invalid_response", "Invalid trust response.");
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new AgentApiException("invalid_response", ex.Message);
        }
    }


    public async Task<string> GetPayloadAsync(string runId, string payloadId, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        ValidatePayloadId(payloadId);
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/payloads/{payloadId}");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected", (int)resp.StatusCode);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "payload_failed", body, (int)resp.StatusCode);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); } catch (JsonException ex) { throw new AgentApiException("invalid_response", ex.Message); }
        var root = doc.RootElement;
        if (root.TryGetProperty("run_id", out var rid) && rid.ValueKind == JsonValueKind.String)
        {
            var returnedRunId = rid.GetString();
            if (!string.Equals(returnedRunId, runId, StringComparison.Ordinal))
                throw new AgentApiException("payload_run_mismatch", "Payload run_id mismatch");
        }
        else
        {
            throw new AgentApiException("invalid_response", "Missing run_id");
        }

        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;
        return body;
    }

    public async Task CancelAsync(string runId, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/cancel");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected", (int)resp.StatusCode);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "cancel_failed", body, (int)resp.StatusCode);
    }

    public async Task ContinueAsync(string runId, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/continue");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "continue_failed", body, (int)resp.StatusCode);
    }

    public async Task<CompactAgentRunResponse> CompactAsync(
        string runId,
        CompactAgentRunRequest? request = null,
        CancellationToken ct = default)
    {
        ValidateRunId(runId);
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/compact");
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request ?? new CompactAgentRunRequest()),
                Encoding.UTF8,
                "application/json"),
        };
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new AgentApiException("unauthorized", "Bearer rejected", (int)resp.StatusCode);
        }

        if (resp.StatusCode == HttpStatusCode.Conflict)
        {
            throw new AgentApiException(
                ExtractErrorCode(body) ?? "conflict",
                body,
                (int)resp.StatusCode);
        }

        if (!resp.IsSuccessStatusCode)
        {
            throw new AgentApiException(
                ExtractErrorCode(body) ?? "compact_failed",
                body,
                (int)resp.StatusCode);
        }

        return ParseCompactResponseStrict(body, runId);
    }

    public async Task<JsonElement> ApproveAsync(string runId, string approvalId, string scope, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        if (string.IsNullOrWhiteSpace(approvalId)) throw new AgentApiException("invalid_approval_id", "approval_id invalid");
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/approvals/{approvalId}");
        var payload = JsonSerializer.Serialize(new { scope, decision = "allow" });
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "approval_failed", body, (int)resp.StatusCode);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public async Task<JsonElement> DenyAsync(string runId, string approvalId, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        if (string.IsNullOrWhiteSpace(approvalId)) throw new AgentApiException("invalid_approval_id", "approval_id invalid");
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/approvals/{approvalId}");
        var payload = JsonSerializer.Serialize(new { decision = "deny", scope = "once" });
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "approval_failed", body, (int)resp.StatusCode);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public async Task<IReadOnlyList<JsonElement>> ListPoliciesAsync(string? scope = null, string? workspaceId = null, CancellationToken ct = default)
    {
        var url = new Uri(_session.BaseUri, BuildPolicyListUrl(scope, workspaceId));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "policy_list_failed", body, (int)resp.StatusCode);
        var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            var list = new List<JsonElement>();
            foreach (var el in items.EnumerateArray()) list.Add(el.Clone());
            return list;
        }
        return Array.Empty<JsonElement>();
    }

    public async Task<JsonElement> CreatePolicyAsync(object policyBody, bool interactive, CancellationToken ct = default)
    {
        var url = new Uri(_session.BaseUri, "agent/v1/policies");
        var json = JsonSerializer.Serialize(policyBody);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        EnsureAuth(req);
        AddInteractiveHeader(req, interactive);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "policy_create_failed", body, (int)resp.StatusCode);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public async Task<JsonElement> SetSessionPresetAsync(string sessionId, string preset, long expectedVersion, bool interactive, CancellationToken ct = default)
    {
        var payload = new { kind = "session_preset", session_id = sessionId, preset, expected_version = expectedVersion };
        return await CreatePolicyAsync(payload, interactive, ct).ConfigureAwait(false);
    }

    public async Task DeletePolicyAsync(string ruleId, bool interactive, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ruleId)) throw new AgentApiException("invalid_rule_id", "rule_id invalid");
        var url = new Uri(_session.BaseUri, $"agent/v1/policies/{ruleId}");
        using var req = new HttpRequestMessage(HttpMethod.Delete, url);
        EnsureAuth(req);
        AddInteractiveHeader(req, interactive);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "policy_delete_failed", body, (int)resp.StatusCode);
    }

    private static string BuildPolicyListUrl(string? scope, string? workspaceId)
    {
        var sb = new StringBuilder("agent/v1/policies");
        var hasQuery = false;
        if (!string.IsNullOrWhiteSpace(scope))
        {
            sb.Append(hasQuery ? "&" : "?");
            sb.Append("scope=").Append(Uri.EscapeDataString(scope));
            hasQuery = true;
        }
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            sb.Append(hasQuery ? "&" : "?");
            sb.Append("workspace_id=").Append(Uri.EscapeDataString(workspaceId));
            hasQuery = true;
        }
        return sb.ToString();
    }

    private static CreateAgentRunResponse ParseCreateResponseStrict(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException ex) { throw new AgentApiException("invalid_response", ex.Message); }
        var root = doc.RootElement;
        var allowed = new HashSet<string>(["run_id", "session_id", "workspace_id", "status", "preset", "created_at", "updated_at", "policy_snapshot_version", "policy_snapshot_sha256", "version", "last_sequence", "history_wait_kind", "terminal_at", "terminal_error_code", "trust_request_id", "display_identity_hash"], StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
        {
            if (!allowed.Contains(prop.Name))
                throw new AgentApiException("unknown_field", $"Unexpected field {prop.Name}");
        }
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<CreateAgentRunResponse>(json, options);
            if (result is null || string.IsNullOrWhiteSpace(result.RunId) || string.IsNullOrWhiteSpace(result.SessionId))
                throw new AgentApiException("invalid_response", "Missing ids");
            ValidateRunId(result.RunId);
            return result;
        }
        catch (JsonException ex) { throw new AgentApiException("invalid_response", ex.Message); }
    }

    private static GetAgentRunResponse ParseGetResponseStrict(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException ex) { throw new AgentApiException("invalid_response", ex.Message); }
        var root = doc.RootElement;
        var allowed = new HashSet<string>(["run_id", "session_id", "workspace_id", "status", "history_wait_kind", "preset", "policy_snapshot_version", "policy_snapshot_sha256", "version", "last_sequence", "created_at", "updated_at", "terminal_at", "terminal_error_code", "trust_request_id", "display_identity_hash"], StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
        {
            if (!allowed.Contains(prop.Name))
                throw new AgentApiException("unknown_field", $"Unexpected field {prop.Name}");
        }
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<GetAgentRunResponse>(json, options);
            if (result is null || string.IsNullOrWhiteSpace(result.RunId))
                throw new AgentApiException("invalid_response", "Missing run_id");
            ValidateRunId(result.RunId);
            if (result.PolicySnapshotVersion <= 0) throw new AgentApiException("invalid_response", "invalid snapshot version");
            return result;
        }
        catch (JsonException ex) { throw new AgentApiException("invalid_response", ex.Message); }
    }

    private static CompactAgentRunResponse ParseCompactResponseStrict(
        string json,
        string expectedRunId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new AgentApiException("invalid_response", exception.Message);
        }

        using (document)
        {
            var allowed = new HashSet<string>(
                ["run_id", "status", "strategy", "compaction_id", "before_tokens", "after_tokens", "run_version"],
                StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!allowed.Contains(property.Name))
                {
                    throw new AgentApiException(
                        "unknown_field",
                        $"Unexpected field {property.Name}");
                }
            }
        }

        try
        {
            var result = JsonSerializer.Deserialize<CompactAgentRunResponse>(json, JsonOptions);
            if (result is null
                || !string.Equals(result.RunId, expectedRunId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(result.Status)
                || result.Strategy is not ("normal" or "aggressive")
                || result.RunVersion < 0
                || (result.CompactionId is not null && !IsBoundedToken(result.CompactionId))
                || (result.BeforeTokens is null) != (result.AfterTokens is null)
                || (result.BeforeTokens is not null
                    && result.AfterTokens is not null
                    && (result.BeforeTokens < 0 || result.AfterTokens < 0
                        || result.AfterTokens > result.BeforeTokens)))
            {
                throw new AgentApiException("invalid_response", "Invalid compaction response.");
            }

            ValidateRunId(result.RunId);
            return result;
        }
        catch (JsonException exception)
        {
            throw new AgentApiException("invalid_response", exception.Message);
        }
    }

    private static string? ExtractErrorCode(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error_code", out var code) && code.ValueKind == JsonValueKind.String)
                return code.GetString();
        }
        catch { }
        return null;
    }

    private static void ValidateRunId(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)
            || !runId.StartsWith("run_", StringComparison.Ordinal)
            || runId.Length != 36
            || !IsLowerHex(runId.AsSpan(4)))
            throw new AgentApiException("invalid_run_id", "run_id invalid");
    }

    private static void ValidatePayloadId(string payloadId)
    {
        if (string.IsNullOrWhiteSpace(payloadId)
            || !payloadId.StartsWith("payload_", StringComparison.Ordinal)
            || payloadId.Length != 40
            || !IsLowerHex(payloadId.AsSpan(8)))
            throw new AgentApiException("invalid_payload_id", "payload_id invalid");
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBoundedToken(string value) =>
        value is { Length: >= 1 and <= 128 }
        && value.All(character =>
            character is (>= 'a' and <= 'z')
                or (>= 'A' and <= 'Z')
                or (>= '0' and <= '9')
                or '-'
                or '_'
                or '.');
}
