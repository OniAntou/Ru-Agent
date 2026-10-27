using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Client;

public sealed partial class AgentApiClient : IAgentPolicyClient, IDisposable
{
    private const int MaxCreateRunAttempts = 2;
    private static readonly TimeSpan CreateRunAttemptDeadline = TimeSpan.FromSeconds(15);
    private const int MaxProviderBackendChars = 64;
    private const int MaxModelsDevProviderIdChars = 64;
    private const int MaxProviderModelChars = 256;
    private static readonly int MaxModelsDevBackendChars =
        ProviderCredential.ModelsDevPrefix.Length + MaxModelsDevProviderIdChars;
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

        // No DefaultRequestHeaders here: a shared client may serve more than one
        // session, and EnsureAuth already attaches the bearer per request. Setting
        // a default would make one session's token leak onto another's calls.
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
        ct.ThrowIfCancellationRequested();
        var url = new Uri(_session.BaseUri, "agent/v1/runs");
        var json = JsonSerializer.Serialize(request);
        Exception? lastTransportError = null;
        var hasTransportUncertainty = false;
        for (var attempt = 0; attempt < MaxCreateRunAttempts; attempt++)
        {
            try
            {
                using var attemptCts = new CancellationTokenSource();
                attemptCts.CancelAfter(CreateRunAttemptDeadline);
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                EnsureAuth(httpRequest);
                using var resp = await _httpClient.SendAsync(httpRequest, attemptCts.Token).ConfigureAwait(false);
                string body;
                try
                {
                    body = await BoundedHttpResponseReader.ReadAsync(resp.Content, attemptCts.Token).ConfigureAwait(false);
                }
                catch (AgentApiException ex) when (
                    resp.IsSuccessStatusCode
                    || hasTransportUncertainty
                    || (int)resp.StatusCode >= 500)
                {
                    throw WithUnknownCreateOutcome(ex, (int)resp.StatusCode);
                }

                return ParseCreateRunResponse(resp, body, request, hasTransportUncertainty);
            }
            catch (HttpRequestException ex)
            {
                lastTransportError = ex;
                hasTransportUncertainty = true;
            }
            catch (OperationCanceledException ex)
            {
                lastTransportError = ex;
                hasTransportUncertainty = true;
            }
        }

        throw new AgentApiException(
            "agent_create_outcome_unknown",
            "The Host may have created this run, but its acknowledgement could not be received.",
            inner: lastTransportError,
            isCreateOutcomeUnknown: true);
    }

    private static CreateAgentRunResponse ParseCreateRunResponse(
        HttpResponseMessage resp,
        string body,
        CreateAgentRunRequest request,
        bool hasTransportUncertainty)
    {
        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException(
                "unauthorized", "Bearer rejected", (int)resp.StatusCode,
                isCreateOutcomeUnknown: hasTransportUncertainty);
        if ((int)resp.StatusCode == 409)
        {
            var blocking = ExtractBlockingRun(body);
            throw new AgentApiException(
                ExtractErrorCode(body) ?? "conflict",
                ExtractErrorMessage(body) ?? body,
                (int)resp.StatusCode,
                blockingRunId: blocking.RunId,
                blockingRunStatus: blocking.Status,
                isCreateOutcomeUnknown: hasTransportUncertainty);
        }
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(
                ExtractErrorCode(body) ?? "agent_create_failed",
                body,
                (int)resp.StatusCode,
                isCreateOutcomeUnknown: hasTransportUncertainty || (int)resp.StatusCode >= 500);

        try
        {
            return ParseCreateResponseStrict(body, request.SessionId, request.Preset);
        }
        catch (AgentApiException ex)
        {
            throw WithUnknownCreateOutcome(ex, (int)resp.StatusCode);
        }
    }

    private static AgentApiException WithUnknownCreateOutcome(AgentApiException error, int statusCode) =>
        new(
            error.ErrorCode,
            error.Message,
            error.StatusCode ?? statusCode,
            error,
            error.BlockingRunId,
            error.BlockingRunStatus,
            error.PresetSessionId,
            error.CurrentPresetVersion,
            isCreateOutcomeUnknown: true);

    public async Task<GetAgentRunResponse> GetRunAsync(string runId, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await BoundedHttpResponseReader.ReadAsync(resp.Content, ct).ConfigureAwait(false);
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
        var body = await ReadBoundedResponseAsync(resp.Content, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Lists the run ids of a session oldest-first. Fail-closed: any transport or
    /// shape error surfaces as AgentApiException and the caller keeps the
    /// text-only transcript instead of a partial activity feed.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListSessionRunIdsAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new AgentApiException("invalid_session_id", "Session id invalid.");
        var url = new Uri(_session.BaseUri, $"agent/v1/sessions/{Uri.EscapeDataString(sessionId)}/runs?limit=100");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await ReadBoundedResponseAsync(resp.Content, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected", (int)resp.StatusCode);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "session_runs_failed", body, (int)resp.StatusCode);

        try
        {
            var result = JsonSerializer.Deserialize<SessionRunsResponse>(body, JsonOptions);
            if (result?.Items is null)
                throw new AgentApiException("invalid_response", "Missing session run items.");
            // Newest-first on the wire; the transcript aligns oldest-first.
            return result.Items.Select(item => item.RunId).Reverse().ToArray();
        }
        catch (JsonException ex)
        {
            throw new AgentApiException("invalid_response", ex.Message);
        }
    }

    /// <summary>
    /// Replays a run's durable activity rows (tool + thinking) in feed order.
    /// Each row parses through the same wire codec as the live transient frames,
    /// so replayed rows render exactly like the ones watched live.
    /// </summary>
    public async Task<IReadOnlyList<AgentActivityModel>> ListRunActivitiesAsync(string runId, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/activities");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await ReadBoundedResponseAsync(resp.Content, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected", (int)resp.StatusCode);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "run_activities_failed", body, (int)resp.StatusCode);

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("activities", out var items)
                || items.ValueKind != JsonValueKind.Array)
                throw new AgentApiException("invalid_response", "Missing run activities.");
            var rows = new List<AgentActivityModel>();
            foreach (var item in items.EnumerateArray())
            {
                AgentActivityWire wire;
                try
                {
                    wire = AgentActivityWireCodec.Parse(item.GetRawText());
                }
                catch (JsonException)
                {
                    continue;
                }

                rows.Add(new AgentActivityModel(
                    wire.ActivityId,
                    wire.Kind,
                    wire.State,
                    wire.FileName,
                    wire.LineStart,
                    wire.LineEnd,
                    wire.Symbol,
                    wire.Count,
                    wire.Added,
                    wire.Removed,
                    wire.DurationMs,
                    wire.SafeCode,
                    wire.ToolDisplay,
                    DurableSequence: wire.DurableSequence,
                    TransientSequence: null,
                    wire.Detail,
                    wire.Todos));
            }

            return rows;
        }
        catch (JsonException ex)
        {
            throw new AgentApiException("invalid_response", ex.Message);
        }
    }

    public async Task<string> GetPayloadAsync(string runId, string payloadId, CancellationToken ct = default)
    {
        var payload = await FetchPayloadEnvelopeAsync(runId, payloadId, ct).ConfigureAwait(false);
        return payload.Content;
    }

    /// <summary>
    /// Fetches a payload together with the encoding the host declared for it
    /// ("utf8text"/"json" text or "opaque" base64 on the wire), so byte-oriented
    /// callers can decode the content once instead of guessing from the body.
    /// </summary>
    public async Task<(string Encoding, string Content)> GetPayloadEnvelopeAsync(string runId, string payloadId, CancellationToken ct = default)
    {
        var payload = await FetchPayloadEnvelopeAsync(runId, payloadId, ct).ConfigureAwait(false);
        return (payload.Encoding ?? string.Empty, payload.Content);
    }

    /// <summary>Retains the payload type so callers never infer it from user-visible text.</summary>
    public Task<AgentPayloadContent> GetPayloadContentAsync(string runId, string payloadId, CancellationToken ct = default) =>
        FetchPayloadEnvelopeAsync(runId, payloadId, ct);

    private async Task<AgentPayloadContent> FetchPayloadEnvelopeAsync(string runId, string payloadId, CancellationToken ct)
    {
        ValidateRunId(runId);
        ValidatePayloadId(payloadId);
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/payloads/{payloadId}");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await ReadBoundedResponseAsync(resp.Content, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected", (int)resp.StatusCode);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "payload_failed", body, (int)resp.StatusCode);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); } catch (JsonException ex) { throw new AgentApiException("invalid_response", ex.Message); }
        using (doc)
        {
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

            var encoding = root.TryGetProperty("encoding", out var encodingElement) && encodingElement.ValueKind == JsonValueKind.String
                ? encodingElement.GetString()
                : null;
            var kind = root.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String
                ? kindElement.GetString()
                : null;
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                return new(kind ?? string.Empty, encoding ?? string.Empty, content.GetString() ?? string.Empty);
            }

            return new(kind ?? string.Empty, encoding ?? string.Empty, body);
        }
    }

    public async Task CancelAsync(string runId, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/cancel");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await ReadBoundedResponseAsync(resp.Content, ct).ConfigureAwait(false);
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
        var body = await ReadBoundedResponseAsync(resp.Content, ct).ConfigureAwait(false);
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
        var body = await ReadBoundedResponseAsync(resp.Content, ct).ConfigureAwait(false);
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

    public async Task<DispatchAgentCommandResponse> DispatchCommandAsync(
        string runId,
        string text,
        CancellationToken ct = default)
    {
        ValidateRunId(runId);
        if (text is null || Encoding.UTF8.GetByteCount(text) > AgentActionRouter.MaxInputBytes)
        {
            throw new AgentApiException("command_invalid", "Command text is too large or missing.");
        }

        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/commands");
        var json = JsonSerializer.Serialize(new DispatchAgentCommandRequest(text));
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await ReadBoundedResponseAsync(resp.Content, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new AgentApiException(
                resp.StatusCode == HttpStatusCode.Unauthorized ? "unauthorized" : ExtractErrorCode(body) ?? "forbidden",
                body,
                (int)resp.StatusCode);
        }

        if (!resp.IsSuccessStatusCode)
        {
            throw new AgentApiException(
                ExtractErrorCode(body) ?? "command_failed",
                body,
                (int)resp.StatusCode);
        }

        return ParseDispatchCommandResponseStrict(body, runId);
    }

    public async Task<JsonElement> ApproveAsync(string runId, string approvalId, string scope, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        if (string.IsNullOrWhiteSpace(approvalId)) throw new AgentApiException("invalid_approval_id", "approval_id invalid");
        // Session scope exists only for external-directory approvals; the server
        // fails anything else closed, so map explicitly and keep once as default.
        // "always" authorizes once and remembers the exact intent as a
        // project-scope allow rule.
        string command = string.Equals(scope, "session", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scope, "allow_session_directory", StringComparison.OrdinalIgnoreCase)
            ? "allow_session_directory"
            : string.Equals(scope, "always", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scope, "allow_always", StringComparison.OrdinalIgnoreCase)
            ? "allow_always"
            : "allow_once";
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/approvals/{approvalId}");
        var payload = JsonSerializer.Serialize(new { command });
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await ReadBoundedResponseAsync(resp.Content, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "approval_failed", body, (int)resp.StatusCode);
        return ParseJsonClone(body);
    }

    public async Task<JsonElement> DenyAsync(string runId, string approvalId, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        if (string.IsNullOrWhiteSpace(approvalId)) throw new AgentApiException("invalid_approval_id", "approval_id invalid");
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/approvals/{approvalId}");
        var payload = JsonSerializer.Serialize(new { command = "deny" });
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await ReadBoundedResponseAsync(resp.Content, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "approval_failed", body, (int)resp.StatusCode);
        return ParseJsonClone(body);
    }

    public async Task<JsonElement> AnswerAsync(string runId, string inputId, string answer, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        if (string.IsNullOrWhiteSpace(inputId)) throw new AgentApiException("invalid_input_id", "input_id invalid");
        if (string.IsNullOrEmpty(answer)) throw new AgentApiException("invalid_answer", "answer invalid");
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/inputs/{inputId}");
        var payload = JsonSerializer.Serialize(new { answer });
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await ReadBoundedResponseAsync(resp.Content, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "input_failed", body, (int)resp.StatusCode);
        return ParseJsonClone(body);
    }

    private static async Task<string> ReadBoundedResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
        => await BoundedHttpResponseReader.ReadAsync(content, cancellationToken).ConfigureAwait(false);

    private static string? ExtractErrorCode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error_code", out var code) && code.ValueKind == JsonValueKind.String)
                return code.GetString();
            if (doc.RootElement.TryGetProperty("code", out var commandCode) && commandCode.ValueKind == JsonValueKind.String)
                return commandCode.GetString();
            if (doc.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                return title.GetString();
        }
        catch { }
        return null;
    }

    private static string? ExtractErrorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                return msg.GetString();
            if (doc.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                return detail.GetString();
        }
        catch { }
        return null;
    }

    private static (string? SessionId, long? Version) ExtractPresetVersionConflict(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var sessionId = root.TryGetProperty("session_id", out var session)
                && session.ValueKind == JsonValueKind.String
                ? session.GetString()
                : null;
            var version = root.TryGetProperty("current_version", out var current)
                && current.ValueKind == JsonValueKind.Number
                && current.TryGetInt64(out var parsed)
                && parsed >= 0
                ? parsed
                : (long?)null;
            return (sessionId, version);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Busy conflicts name the run that holds the workspace or session so callers
    /// can point the operator at the exact run to cancel or attach to.
    /// </summary>
    private static (string? RunId, string? Status) ExtractBlockingRun(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var runId = doc.RootElement.TryGetProperty("blocking_run_id", out var id)
                && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
            var status = doc.RootElement.TryGetProperty("blocking_run_status", out var runStatus)
                && runStatus.ValueKind == JsonValueKind.String
                ? runStatus.GetString()
                : null;
            return (runId, status);
        }
        catch
        {
            return (null, null);
        }
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
