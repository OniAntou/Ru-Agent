using System.Text;
using System.Text.Json;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Steering;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Harness.Persistence.Policy;
using RuBackend.Business.Sessions;
using RuBackend.Harness.Workspace;
using RuAgent.Protocol.Harness;
using static RuBackend.Harness.AgentV1Shared;

namespace RuBackend.Harness;

public sealed class AgentRunQueryService
{
    private readonly IHarnessEventStore _store;
    private readonly ISessionStore _sessions;
    private readonly AgentWorkspaceTrustService _workspaceTrust;
    private readonly AgentV1SubagentGuard _subagentGuard;
    private readonly SqliteAgentRunProviderSelectionStore _providerSelections;
    private readonly HarnessPayloadStore _payloadStore;
    private readonly HarnessSqliteOptions _options;
    private readonly ILogger<AgentRunQueryService>? _logger;
    private readonly RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor _secrets;

    public AgentRunQueryService(IHarnessEventStore store, ISessionStore sessions,
        AgentWorkspaceTrustService workspaceTrust, AgentV1SubagentGuard subagentGuard,
        SqliteAgentRunProviderSelectionStore providerSelections, HarnessPayloadStore payloadStore,
        HarnessSqliteOptions options, ILogger<AgentRunQueryService>? logger = null,
        RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor? secrets = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _workspaceTrust = workspaceTrust ?? throw new ArgumentNullException(nameof(workspaceTrust));
        _subagentGuard = subagentGuard ?? throw new ArgumentNullException(nameof(subagentGuard));
        _providerSelections = providerSelections ?? throw new ArgumentNullException(nameof(providerSelections));
        _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _secrets = secrets ?? RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor.Empty;
    }

    /// <summary>The redactor a whole reasoning read passes through; the Host's own known-secret set.</summary>
    internal RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor Secrets => _secrets;
    public async Task<AgentServiceResult> GetRunAsync(string runIdRaw, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        var run = await _store.GetRunAsync(new RunId(runIdRaw), cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        var (trustRequestId, displayHash) = await _workspaceTrust.ResolveTrustChallengeAsync(run, cancellationToken).ConfigureAwait(false);
        var isWaitingInput = await IsParkedOnInputAsync(run, cancellationToken).ConfigureAwait(false);
        var providerSelection = await _providerSelections.GetAsync(run.RunId, cancellationToken).ConfigureAwait(false);
        var response = WithProviderMetadata(MapRun(run, trustRequestId, displayHash, isWaitingInput),
            providerSelection?.Selection) with
        {
            ImagePayloads = await ReadImagePayloadsAsync(run.RunId, cancellationToken).ConfigureAwait(false),
        };
        return new AgentServiceResult(200, response);
    }

    /// <summary>
    /// The run's user images, grouped by the message that carried them: the prompt's images
    /// first, then each delivered steer's in delivery order. A delivered steer's image rows
    /// name its images: delivery stores each as the <c>UserImage</c> whose id derives from
    /// the row (<see cref="RunSteerContract.DeliveredImagePayloadId"/>). A steer delivered
    /// before that kept its <c>SteerImage</c> payload and an unnamed <c>UserImage</c> copy, so
    /// that many unnamed copies are left out of the prompt's images. The rest are the
    /// prompt's, in the order they were stored. Null when the run carried no image.
    /// </summary>
    internal async Task<IReadOnlyList<AgentRunImagePayload>?> ReadImagePayloadsAsync(
        RunId runId, CancellationToken cancellationToken)
    {
        await using var conn = await OpenHarnessConnectionAsync(cancellationToken).ConfigureAwait(false);
        var userImages = new List<string>();
        await using (var command = conn.CreateCommand())
        {
            command.CommandText = "SELECT payload_id FROM agent_payloads WHERE run_id=$run AND kind=$kind ORDER BY rowid";
            command.Parameters.AddWithValue("$run", runId.Value);
            command.Parameters.AddWithValue("$kind", (int)HarnessPayloadKind.UserImage);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) userImages.Add(reader.GetString(0));
        }

        var steerImages = new List<AgentRunImagePayload>();
        await using (var command = conn.CreateCommand())
        {
            command.CommandText = """
                SELECT i.steer_id, i.payload_id FROM agent_run_steer_images i
                JOIN agent_run_steers s ON s.steer_id = i.steer_id
                WHERE s.run_id=$run AND s.delivered_event_id IS NOT NULL
                ORDER BY s.delivered_at_utc, s.created_at_utc, s.steer_id, i.ordinal
                """;
            command.Parameters.AddWithValue("$run", runId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                steerImages.Add(new AgentRunImagePayload(reader.GetString(1), reader.GetString(0)));
        }

        var stored = new HashSet<string>(userImages, StringComparer.Ordinal);
        var delivered = new HashSet<string>(StringComparer.Ordinal);
        var legacyCopies = 0;
        for (var index = 0; index < steerImages.Count; index++)
        {
            var derived = RunSteerContract.DeliveredImagePayloadId(new PayloadId(steerImages[index].PayloadId)).Value;
            if (stored.Contains(derived))
            {
                steerImages[index] = steerImages[index] with { PayloadId = derived };
                delivered.Add(derived);
            }
            else
            {
                legacyCopies++;
            }
        }

        var unnamed = userImages.Where(id => !delivered.Contains(id)).ToList();
        var promptCount = Math.Max(0, unnamed.Count - legacyCopies);
        if (promptCount == 0 && steerImages.Count == 0) return null;
        return unnamed.Take(promptCount).Select(id => new AgentRunImagePayload(id)).Concat(steerImages).ToArray();
    }

    /// <summary>
    /// Untrusted runs expose the pending trust challenge so clients can approve the
    /// workspace they actually created; trusted or terminal runs carry no challenge.
    /// </summary>

    public async Task<AgentServiceResult> ListSessionRunsAsync(string sessionIdRaw, int? limitRaw, string? cursor, CancellationToken cancellationToken)
    {
        if (!IsSessionId(sessionIdRaw))
        {
            return BoundedError(400, "invalid_session_id", "session_id invalid.");
        }

        if (await _subagentGuard.IsInternalSessionAsync(sessionIdRaw, cancellationToken).ConfigureAwait(false))
        {
            return BoundedError(404, "session_not_found", "Session not found.");
        }

        var sessionDoc = await _sessions.GetAsync(sessionIdRaw, cancellationToken).ConfigureAwait(false);
        if (sessionDoc is null)
        {
            return BoundedError(404, "session_not_found", "Session not found.");
        }

        int limit = limitRaw ?? 50;
        if (limit is < 1 or > 100)
        {
            return BoundedError(400, "invalid_limit", "limit must be 1..100.");
        }

        string? anchorCreatedAt = null;
        string? anchorRunId = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var decoded = TryDecodeCursor(cursor);
            if (decoded is null)
            {
                return BoundedError(400, "invalid_cursor", "cursor invalid.");
            }

            anchorCreatedAt = decoded.Value.CreatedAt;
            anchorRunId = decoded.Value.RunId;

            if (!IsRunId(anchorRunId!) || !IsValidIsoTimestamp(anchorCreatedAt!))
            {
                return BoundedError(400, "invalid_cursor", "cursor invalid.");
            }

            var anchorRun = await _store.GetRunAsync(new RunId(anchorRunId!), cancellationToken).ConfigureAwait(false);
            if (anchorRun is null || anchorRun.SessionId.Value != sessionIdRaw)
            {
                return BoundedError(400, "invalid_cursor", "cursor invalid.");
            }
        }

        var runs = new List<AgentRun>();
        await using var conn = await OpenHarnessConnectionAsync(cancellationToken).ConfigureAwait(false);
        var sql = new StringBuilder();
        sql.Append("SELECT run_id, session_id, workspace_id, state, history_wait_kind, version, last_sequence, budget_window, model_steps_in_window, tool_calls_in_window, preset, permission_profile_version, policy_snapshot_version, policy_snapshot_sha256, created_at_utc, updated_at_utc, terminal_at_utc, terminal_error_code FROM agent_runs WHERE session_id = $session_id ");
        if (anchorCreatedAt is not null && anchorRunId is not null)
        {
            sql.Append("AND ((created_at_utc < $anchor_created) OR (created_at_utc = $anchor_created AND run_id < $anchor_run_id)) ");
        }

        sql.Append("ORDER BY created_at_utc DESC, run_id DESC LIMIT $limit_plus_one");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue("$session_id", sessionIdRaw);
        if (anchorCreatedAt is not null)
        {
            cmd.Parameters.AddWithValue("$anchor_created", anchorCreatedAt);
            cmd.Parameters.AddWithValue("$anchor_run_id", anchorRunId!);
        }

        cmd.Parameters.AddWithValue("$limit_plus_one", limit + 1);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            runs.Add(ReadAgentRun(reader));
        }

        string? nextCursor = null;
        if (runs.Count > limit)
        {
            var last = runs[limit - 1];
            nextCursor = EncodeCursor(last.CreatedAt.ToUniversalTime().ToString("O"), last.RunId.Value);
            runs = runs.Take(limit).ToList();
        }

        var providerSelections = await _providerSelections.GetManyAsync(
            runs.Select(run => run.RunId).ToArray(), cancellationToken).ConfigureAwait(false);
        var items = runs.Select(run => WithProviderMetadata(MapRun(run),
            providerSelections.GetValueOrDefault(run.RunId)?.Selection)).ToList();
        var resp = new SessionRunsResponse(items, nextCursor);
        return new AgentServiceResult(200, resp);
    }

    public async Task<AgentServiceResult> GetPayloadAsync(string runIdRaw, string payloadIdRaw, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw) || !IsPayloadId(payloadIdRaw))
        {
            return BoundedError(400, "invalid_id", "run or payload id invalid.");
        }

        var runId = new RunId(runIdRaw);
        var payloadId = new PayloadId(payloadIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        await using var conn = await OpenHarnessConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT kind, audience FROM agent_payloads WHERE run_id = $run_id AND payload_id = $payload_id";
        checkCmd.Parameters.AddWithValue("$run_id", runIdRaw);
        checkCmd.Parameters.AddWithValue("$payload_id", payloadIdRaw);
        await using var reader = await checkCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return BoundedError(404, "payload_not_found", "Payload not found.");
        }

        var kind = (HarnessPayloadKind)reader.GetInt32(0);
        var audience = (HarnessPayloadAudience)reader.GetInt32(1);
        if (kind == HarnessPayloadKind.RecoveryOpaque)
        {
            return BoundedError(404, "payload_not_found", "Payload not found.");
        }

        if (!audience.HasFlag(HarnessPayloadAudience.AuthenticatedClient))
        {
            return BoundedError(404, "payload_not_found", "Payload not found.");
        }

        try
        {
            var content = await _payloadStore.ReadForClientAsync(runId, payloadId, cancellationToken).ConfigureAwait(false);
            var payloadJson = content.Encoding == HarnessPayloadEncoding.Opaque
                ? Convert.ToBase64String(content.Plaintext.ToArray())
                : Encoding.UTF8.GetString(content.Plaintext.Span);
            // A thinking row's whole reasoning gets the same redaction as its excerpt: the
            // stored text was redacted when written, and a secret configured since is hidden too.
            if (kind == HarnessPayloadKind.ModelReasoning)
            {
                payloadJson = _secrets.Redact(payloadJson);
            }
            // Never silently truncate a payload: callers may persist the returned
            // value as a prompt or recovery artifact, so truncation would turn a
            // valid document/base64 blob into corrupt data.  Keep the response
            // bounded by rejecting oversized payloads explicitly instead.
            // An image is a data URL up to the per-image bound, which is larger than the
            // general limit; the response still fits a client's 8 MiB reader.
            int maxClientPayloadCharacters = kind is HarnessPayloadKind.UserImage or HarnessPayloadKind.SteerImage
                ? RuAgent.Protocol.Harness.AgentRunLimits.MaxImageDataUrlChars
                : 4 * 1024 * 1024;
            if (payloadJson.Length > maxClientPayloadCharacters)
            {
                return BoundedError(413, "payload_too_large", "Payload exceeds the client response limit.");
            }

            var body = new
            {
                payload_id = payloadIdRaw,
                run_id = runIdRaw,
                kind = kind.ToString().ToLowerInvariant(),
                encoding = content.Encoding.ToString().ToLowerInvariant(),
                content = payloadJson,
            };
            return new AgentServiceResult(200, body);
        }
        catch (HarnessStoreException ex) when (ex.Code is "payload_not_found" or "payload_client_forbidden" or "payload_recovery_forbidden")
        {
            return BoundedError(404, "payload_not_found", "Payload not found.");
        }
        catch (HarnessStoreException ex)
        {
            _logger?.LogWarning(ex, "payload read failed");
            return BoundedError(500, ex.Code ?? "payload_error", "Unable to read payload.");
        }
    }

    /// <summary>
    /// Đọc usage payload (kind ModelUsage) cho SSE enrich. Fail-closed: mọi lỗi
    /// đều trả null, không throw, không log payload bytes.
    /// </summary>
    public async Task<RuAgent.Harness.Core.ModelUsage?> TryReadModelUsageAsync(string runIdRaw, string payloadIdRaw, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsRunId(runIdRaw) || !IsPayloadId(payloadIdRaw)) return null;
            var content = await _payloadStore.ReadForClientAsync(new RunId(runIdRaw), new PayloadId(payloadIdRaw), cancellationToken).ConfigureAwait(false);
            if (content.Encoding == HarnessPayloadEncoding.Opaque) return null;
            if (content.Plaintext.Length > 4096) return null;
            using var doc = JsonDocument.Parse(content.Plaintext);
            return RuAgent.Harness.Core.ModelUsage.Read(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private Task<SqliteConnection> OpenHarnessConnectionAsync(CancellationToken cancellationToken) =>
        // Single construction path: HarnessSqliteOptions owns the string, the
        // private-cache mode and the dispose-on-open-failure guarantee, so this
        // copy cannot drift from it again.
        _options.OpenConnectionAsync(cancellationToken);

    private static AgentRun ReadAgentRun(SqliteDataReader reader, int startIndex = 0) =>
        RuAgent.Harness.Persistence.HarnessSqliteMapper.ReadRun(reader, startIndex);

    private async Task<bool> IsParkedOnInputAsync(AgentRun run, CancellationToken cancellationToken)
    {
        if (run.State != HarnessRunState.WaitingUser)
        {
            return false;
        }

        // While a run is parked nothing drives it, so the newest durable event is
        // the park cause. One indexed row distinguishes an ask_user wait from the
        // budget and transient-provider waits that share the WaitingUser state.
        await using var connection = await OpenHarnessConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind FROM agent_events WHERE run_id = $run_id ORDER BY sequence DESC LIMIT 1";
        command.Parameters.AddWithValue("$run_id", run.RunId.Value);
        var kind = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return string.Equals(kind, "input.requested", StringComparison.Ordinal);
    }

    // Helpers (slice 1: pure logic lives in AgentV1Shared; wrappers keep API stable)

    // Single and paged reads expose the same pinned display/context fields.
    // Endpoint addresses, credential accounts and raw request options stay private.
    private static GetAgentRunResponse WithProviderMetadata(GetAgentRunResponse response,
        AgentRunProviderSelection? selection) => response with
    {
        ProviderBackend = selection?.Backend,
        ProviderModel = selection?.Model,
        ProviderProtocol = selection?.Protocol,
        ProviderModelLabel = selection?.ModelLabel,
        ProviderCallingMode = selection?.CallingMode,
        ContextWindowTokens = selection?.ContextWindowTokens,
        ContextWindowSource = selection?.ContextWindowSource,
        ContextWindowVersion = selection?.ContextWindowVersion,
        OutputTokenReserve = selection?.OutputTokenReserve,
        ToolProfile = selection is null ? null : RuAgent.Protocol.AgentToolProfiles.ReadSupportedLabel(
            System.Text.Json.Nodes.JsonNode.Parse(selection.RequestOptionsJson)!.AsObject()),
    };

    internal static GetAgentRunResponse MapRun(
        AgentRun run,
        string? trustRequestId = null,
        string? displayIdentityHash = null,
        bool isWaitingInput = false) => new(
        RunId: run.RunId.Value,
        SessionId: run.SessionId.Value,
        WorkspaceId: run.WorkspaceId.Value,
        Status: MapRunState(run.Status, isWaitingInput),
        HistoryWaitKind: MapHistoryWaitKind(run.HistoryWaitKind),
        Preset: AgentPermissionPresetMapper.ToWireValue(run.Preset),
        PolicySnapshotVersion: run.PolicySnapshotVersion,
        PolicySnapshotSha256: run.PolicySnapshotSha256,
        Version: run.Version,
        LastSequence: run.LastSequence,
        CreatedAt: run.CreatedAt.ToUniversalTime().ToString("O"),
        UpdatedAt: run.UpdatedAt.ToUniversalTime().ToString("O"),
        TerminalAt: run.TerminalAt?.ToUniversalTime().ToString("O"),
        TerminalErrorCode: run.TerminalErrorCode,
        TrustRequestId: trustRequestId,
        DisplayIdentityHash: displayIdentityHash,
        FailureKind: run.TerminalFailure?.Kind,
        FailureSite: run.TerminalFailure?.Site);

}
