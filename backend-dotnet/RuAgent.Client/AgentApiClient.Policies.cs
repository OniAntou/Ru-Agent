using System.Text;
using System.Text.Json;

namespace RuAgent.Client;

public sealed partial class AgentApiClient
{
    public async Task<(string Preset, long Version)?> GetSessionPresetAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(_session.BaseUri, $"agent/v1/sessions/{Uri.EscapeDataString(sessionId)}/preset"));
        EnsureAuth(request);
        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var body = await ReadBoundedResponseAsync(response.Content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "session_preset_read_failed", body, (int)response.StatusCode);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("session_id", out var owner) || owner.GetString() != sessionId
                || !root.TryGetProperty("version", out var version) || !version.TryGetInt64(out var number) || number < 0)
                throw new AgentApiException("invalid_response", "Session preset response does not match its owner.");
            if (!root.TryGetProperty("preset", out var selection) || selection.ValueKind == JsonValueKind.Null) return null;
            var preset = selection.GetString();
            if (preset is not ("plan" or "standard" or "full_access"))
                throw new AgentApiException("invalid_response", "Unknown session permission preset.");
            return (preset, number);
        }
        catch (JsonException error) { throw new AgentApiException("invalid_response", error.Message); }
        catch (InvalidOperationException error) { throw new AgentApiException("invalid_response", error.Message); }
    }

    public async Task<IReadOnlyList<JsonElement>> ListPoliciesAsync(string? scope = null, string? workspaceId = null, CancellationToken ct = default)
    {
        var url = new Uri(_session.BaseUri, BuildPolicyListUrl(scope, workspaceId));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await ReadBoundedResponseAsync(resp.Content, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "policy_list_failed", body, (int)resp.StatusCode);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException ex) { throw new AgentApiException("invalid_response", ex.Message); }
        using (doc)
        {
            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                var list = new List<JsonElement>();
                foreach (var el in items.EnumerateArray()) list.Add(el.Clone());
                return list;
            }
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
        var body = await ReadBoundedResponseAsync(resp.Content, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var code = ExtractErrorCode(body) ?? "policy_create_failed";
            var msg = ExtractErrorMessage(body) ?? (string.IsNullOrWhiteSpace(body) ? $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}" : body);
            var presetVersion = code == "preset_version_conflict" && (int)resp.StatusCode == 409
                ? ExtractPresetVersionConflict(body)
                : (SessionId: (string?)null, Version: (long?)null);
            throw new AgentApiException(
                code,
                msg,
                (int)resp.StatusCode,
                presetSessionId: presetVersion.SessionId,
                currentPresetVersion: presetVersion.Version);
        }
        try
        {
            return ParseJsonClone(body);
        }
        catch (JsonException exception)
        {
            throw new AgentApiException("invalid_response", exception.Message);
        }
    }

    public async Task<JsonElement> SetSessionPresetAsync(
        string sessionId,
        string preset,
        long expectedVersion,
        bool interactive,
        CancellationToken ct = default,
        string? snapshotToken = null)
    {
        object payload = snapshotToken is null
            ? new { kind = "session_preset", session_id = sessionId, preset, expected_version = expectedVersion }
            : new { kind = "session_preset", session_id = sessionId, preset, expected_version = expectedVersion, snapshot_token = snapshotToken };
        return await CreatePolicyAsync(payload, interactive, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> RestoreSessionPresetAsync(
        string sessionId,
        string snapshotToken,
        CancellationToken ct = default)
    {
        var payload = new
        {
            kind = "session_preset_restore",
            session_id = sessionId,
            snapshot_token = snapshotToken,
        };
        var response = await CreatePolicyAsync(payload, interactive: false, ct).ConfigureAwait(false);
        return ParseRestoreResponseStrict(response.GetRawText(), sessionId);
    }

    public async Task DeletePolicyAsync(string ruleId, bool interactive, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ruleId)) throw new AgentApiException("invalid_rule_id", "rule_id invalid");
        var url = new Uri(_session.BaseUri, $"agent/v1/policies/{ruleId}");
        using var req = new HttpRequestMessage(HttpMethod.Delete, url);
        EnsureAuth(req);
        AddInteractiveHeader(req, interactive);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await ReadBoundedResponseAsync(resp.Content, ct).ConfigureAwait(false);
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
}
