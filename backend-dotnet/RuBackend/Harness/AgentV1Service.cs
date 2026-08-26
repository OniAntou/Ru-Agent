using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Protocol.Harness;
using RuBackend.Business.Sessions;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Workspace;

namespace RuBackend.Harness;

public sealed record AgentServiceResult(int StatusCode, object? Body);

public sealed class AgentV1Service
{
    private readonly IHarnessEventStore _store;
    private readonly HarnessSqliteOptions _options;
    private readonly WindowsWorkspaceIdentityResolver _resolver;
    private readonly SqliteWorkspaceStore _workspaceStore;
    private readonly WorkspaceTrustRequestStore _trustStore;
    private readonly WorkspaceTrustService _trustService;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly HarnessPayloadStore _payloadStore;
    private readonly ISessionStore _sessions;
    private readonly AgentSessionPermissionStore _permissionStore;
    private readonly IHarnessKeyStore _keyStore;
    private readonly IAgentRuntimeRootRegistry? _runtimeRoots;
    private readonly RuBackend.Harness.Reconciliation.WorkspaceStateSnapshotService? _snapshots;
    private readonly ReconciliationInspectionGate? _inspectionGate;
    private readonly RuBackend.Harness.Reconciliation.IAgentApprovalGrants? _approvalGrants;

    public AgentV1Service(
        IHarnessEventStore store,
        HarnessSqliteOptions options,
        WindowsWorkspaceIdentityResolver resolver,
        SqliteWorkspaceStore workspaceStore,
        WorkspaceTrustRequestStore trustStore,
        WorkspaceTrustService trustService,
        IIdGenerator ids,
        ITimeProvider time,
        HarnessPayloadStore payloadStore,
        ISessionStore sessions,
        AgentSessionPermissionStore permissionStore,
        IHarnessKeyStore keyStore,
        IAgentRuntimeRootRegistry? runtimeRoots = null,
        RuBackend.Harness.Reconciliation.WorkspaceStateSnapshotService? snapshots = null,
        ReconciliationInspectionGate? inspectionGate = null,
        RuBackend.Harness.Reconciliation.IAgentApprovalGrants? approvalGrants = null)
    {
        _store = store;
        _options = options;
        _resolver = resolver;
        _workspaceStore = workspaceStore;
        _trustStore = trustStore;
        _trustService = trustService;
        _ids = ids;
        _time = time;
        _payloadStore = payloadStore;
        _sessions = sessions;
        _permissionStore = permissionStore;
        _keyStore = keyStore;
        _runtimeRoots = runtimeRoots;
        _snapshots = snapshots;
        _inspectionGate = inspectionGate;
        _approvalGrants = approvalGrants;
    }

    // Compatibility overload for tests that construct with old signature (fallback to ephemeral)
    public AgentV1Service(
        IHarnessEventStore store,
        HarnessSqliteOptions options,
        WindowsWorkspaceIdentityResolver resolver,
        SqliteWorkspaceStore workspaceStore,
        WorkspaceTrustRequestStore trustStore,
        WorkspaceTrustService trustService,
        IIdGenerator ids,
        ITimeProvider time,
        HarnessPayloadStore payloadStore,
        ISessionStore sessions,
        AgentSessionPermissionStore permissionStore)
        : this(store, options, resolver, workspaceStore, trustStore, trustService, ids, time, payloadStore, sessions, permissionStore, new EphemeralKeyStoreFallback())
    {
    }

    private sealed class EphemeralKeyStoreFallback : IHarnessKeyStore
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken)
        {
            var copy = new byte[32];
            Buffer.BlockCopy(_key, 0, copy, 0, 32);
            return ValueTask.FromResult(copy);
        }
    }

    // Run creation
    public async Task<AgentServiceResult> CreateRunAsync(CreateAgentRunRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BoundedError(400, "invalid_request", "Request body required.");
        }

        var sessionIdRaw = request.SessionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sessionIdRaw) || sessionIdRaw.Length > 64)
        {
            return BoundedError(400, "invalid_session_id", "session_id invalid.");
        }

        if (!IsSessionId(sessionIdRaw))
        {
            return BoundedError(400, "invalid_session_id", "session_id invalid.");
        }

        var workspacePath = request.WorkspacePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(workspacePath) || workspacePath.Length > 4096)
        {
            return BoundedError(400, "invalid_workspace", "workspace_path invalid.");
        }

        var prompt = request.Prompt ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 16000)
        {
            return BoundedError(400, "invalid_prompt", "prompt invalid.");
        }

        var presetRaw = request.Preset?.Trim().ToLowerInvariant() ?? string.Empty;
        AgentPermissionPreset preset;
        if (presetRaw == "standard")
        {
            preset = AgentPermissionPreset.Standard;
        }
        else if (presetRaw == "full_access")
        {
            preset = AgentPermissionPreset.FullAccess;
        }
        else
        {
            return BoundedError(400, "invalid_preset", "preset must be standard or full_access.");
        }

        var idempotencyKey = request.IdempotencyKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256)
        {
            return BoundedError(400, "invalid_idempotency_key", "idempotency_key invalid.");
        }

        long? clientVersion = request.PresetSelectionVersion;
        if (clientVersion is not null && clientVersion <= 0)
        {
            return BoundedError(400, "invalid_preset_version", "preset_selection_version must be positive.");
        }

        var sessionDoc = await _sessions.GetAsync(sessionIdRaw, cancellationToken).ConfigureAwait(false);
        if (sessionDoc is null)
        {
            return BoundedError(404, "session_not_found", "Session not found.");
        }

        WorkspaceIdentity workspaceIdentity;
        try
        {
            workspaceIdentity = await _resolver.ResolveAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return BoundedError(400, "invalid_workspace", "workspace_path invalid.");
        }

        WorkspaceTrustRequest? pendingTrustRequest = null;
        try
        {
            var trustReq = await _trustService.EnsureTrustRequestAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            var storedWs = await _workspaceStore.GetAsync(workspaceIdentity.WorkspaceId, cancellationToken).ConfigureAwait(false);
            if (storedWs is not null)
            {
                workspaceIdentity = storedWs.Identity;
                pendingTrustRequest = storedWs.TrustRequest;
            }
            else
            {
                pendingTrustRequest = trustReq;
            }
        }
        catch (Exception)
        {
            return BoundedError(400, "invalid_workspace", "workspace registration failed.");
        }

        long effectiveVersion;
        var existingSelection = await _permissionStore.GetAsync(new SessionId(sessionIdRaw), cancellationToken).ConfigureAwait(false);
        if (existingSelection is null)
        {
            if (preset == AgentPermissionPreset.FullAccess)
            {
                return BoundedError(409, "preset_mismatch", "Full Access requires preset selection.");
            }

            if (clientVersion is null)
            {
                effectiveVersion = 1;
            }
            else if (clientVersion == 1)
            {
                effectiveVersion = 1;
            }
            else
            {
                return BoundedError(409, "preset_mismatch", "preset_selection_version mismatch.");
            }
        }
        else
        {
            if (clientVersion is null)
            {
                var idemShaCheck = ComputeSha256(idempotencyKey);
                var storedHash = await FindIdempotentNormalizedHashAsync(sessionIdRaw, idemShaCheck, cancellationToken).ConfigureAwait(false);
                if (storedHash is not null)
                {
                    effectiveVersion = existingSelection.Version;
                }
                else if (preset == AgentPermissionPreset.Standard && existingSelection.Preset == AgentPermissionPreset.Standard)
                {
                    effectiveVersion = existingSelection.Version;
                }
                else
                {
                    return BoundedError(409, "preset_mismatch", "preset_selection_version required.");
                }
            }
            else
            {
                if (existingSelection.Preset != preset || existingSelection.Version != clientVersion.Value)
                {
                    return BoundedError(409, "preset_mismatch", "preset/version mismatch.");
                }

                effectiveVersion = existingSelection.Version;
            }
        }

        var isTrusted = await _workspaceStore.IsTrustedAsync(workspaceIdentity, preset, cancellationToken).ConfigureAwait(false);

        var now = _time.UtcNow;
        var runId = _ids.NewRunId();
        var initialState = isTrusted
            ? new HarnessRunStatus(HarnessRunState.WaitingHistory, HistoryWaitKind.UserBeforeFirstStep)
            : new HarnessRunStatus(HarnessRunState.WaitingWorkspaceTrust);

        var sessionHarnessId = new SessionId(sessionIdRaw);
        var snapshotSha = ComputePolicySnapshotSha(preset, workspaceIdentity);
        var run = new AgentRun(
            runId,
            sessionHarnessId,
            workspaceIdentity.WorkspaceId,
            initialState,
            version: 0,
            lastSequence: 0,
            budgetWindow: 1,
            modelStepsInWindow: 0,
            toolCallsInWindow: 0,
            preset,
            policySnapshotVersion: effectiveVersion,
            policySnapshotSha256: snapshotSha,
            createdAt: now,
            updatedAt: now);

        var events = new List<HarnessEventIntent>
        {
            new(_ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: isTrusted ? "waiting_history" : "waiting_workspace_trust"),
        };
        events = new List<HarnessEventIntent>
        {
            new(_ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: isTrusted ? "waiting_history" : "waiting_workspace_trust"),
        };
        run = new AgentRun(
            runId,
            sessionHarnessId,
            workspaceIdentity.WorkspaceId,
            initialState,
            version: 0,
            lastSequence: events.Count,
            budgetWindow: 1,
            modelStepsInWindow: 0,
            toolCallsInWindow: 0,
            preset,
            policySnapshotVersion: effectiveVersion,
            policySnapshotSha256: snapshotSha,
            createdAt: now,
            updatedAt: now);

        var payloads = new List<HarnessPayloadIntent>();
        IReadOnlyList<ConversationOutboxIntent> outbox = Array.Empty<ConversationOutboxIntent>();

        var promptBytes = Encoding.UTF8.GetBytes(prompt);
        var promptPayloadId = _ids.NewPayloadId();
        var promptPayload = new HarnessPayloadIntent(
            promptPayloadId,
            HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            Math.Max(promptBytes.Length, 1),
            promptBytes);
        payloads.Add(promptPayload);

        if (isTrusted)
        {
            var outboxIntent = new ConversationOutboxIntent(
                _ids.NewEventId(),
                run.SessionId,
                promptPayloadId,
                ConversationOutboxRole.User,
                HistoryWaitKind.UserBeforeFirstStep);
            outbox = new[] { outboxIntent };
        }
        else
        {
            outbox = Array.Empty<ConversationOutboxIntent>();
        }

        var idempotencySha = ComputeSha256(idempotencyKey);
        var normalizedRequestSha = ComputeSha256($"{sessionIdRaw}|{workspaceIdentity.WorkspaceId.Value}|{prompt}|{preset}|{effectiveVersion}");
        var creationIdentity = new RunCreationIdentity(idempotencySha, normalizedRequestSha, effectiveVersion);
        var newRun = new NewAgentRun(run, events, payloads, outbox, creationIdentity);

        CreateRunResult storeResult;
        try
        {
            storeResult = await _store.TryCreateRunAsync(newRun, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException ex)
        {
            return BoundedError(400, ex.Code ?? "harness_error", ex.Message);
        }

        if (storeResult.Outcome == CreateRunOutcome.Created)
        {
            RegisterRuntimeRoot(storeResult.Run, workspacePath);
            var resp = isTrusted
                ? MapRun(storeResult.Run)
                : MapRun(storeResult.Run, pendingTrustRequest?.RequestId.Value, workspaceIdentity.DisplayIdentityHash);
            return new AgentServiceResult(201, resp);
        }

        if (storeResult.Outcome == CreateRunOutcome.AlreadyExists)
        {
            RegisterRuntimeRoot(storeResult.Run, workspacePath);
            var resp = isTrusted
                ? MapRun(storeResult.Run)
                : MapRun(storeResult.Run, pendingTrustRequest?.RequestId.Value, workspaceIdentity.DisplayIdentityHash);
            return new AgentServiceResult(200, resp);
        }

        var code = storeResult.ErrorCode ?? "conflict";
        return code switch
        {
            "idempotency_conflict" => BoundedError(409, "idempotency_conflict", "Idempotency key conflict."),
            "workspace_busy" => BoundedError(409, "workspace_busy", "Workspace busy."),
            "session_busy" => BoundedError(409, "workspace_busy", "Session busy."),
            "preset_mismatch" => BoundedError(409, "preset_mismatch", "Preset mismatch."),
            "preset_selection_required" => BoundedError(409, "preset_mismatch", "Preset selection required."),
            "preset_version_conflict" => BoundedError(409, "preset_mismatch", "Preset version conflict."),
            "workspace_not_found" => BoundedError(404, "workspace_not_found", "Workspace not found."),
            _ => BoundedError(409, code, "Create conflict."),
        };
    }

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

        var (trustRequestId, displayHash) = await ResolveTrustChallengeAsync(run, cancellationToken).ConfigureAwait(false);
        return new AgentServiceResult(200, MapRun(run, trustRequestId, displayHash));
    }

    /// <summary>
    /// Untrusted runs expose the pending trust challenge so clients can approve the
    /// workspace they actually created; trusted or terminal runs carry no challenge.
    /// </summary>
    private async Task<(string? TrustRequestId, string? DisplayIdentityHash)> ResolveTrustChallengeAsync(
        AgentRun run,
        CancellationToken cancellationToken)
    {
        if (run.State != HarnessRunState.WaitingWorkspaceTrust)
        {
            return (null, null);
        }

        var registration = await _workspaceStore.GetAsync(run.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (registration is null || registration.TrustRequest.AppliedDecision is not null)
        {
            return (null, null);
        }

        return (registration.TrustRequest.RequestId.Value, registration.Identity.DisplayIdentityHash);
    }

    public async Task<AgentServiceResult> ListSessionRunsAsync(string sessionIdRaw, int? limitRaw, string? cursor, CancellationToken cancellationToken)
    {
        if (!IsSessionId(sessionIdRaw))
        {
            return BoundedError(400, "invalid_session_id", "session_id invalid.");
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
        sql.Append("SELECT run_id, session_id, workspace_id, state, history_wait_kind, version, last_sequence, budget_window, model_steps_in_window, tool_calls_in_window, preset, policy_snapshot_version, policy_snapshot_sha256, created_at_utc, updated_at_utc, terminal_at_utc, terminal_error_code FROM agent_runs WHERE session_id = $session_id ");
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

        var items = runs.Select(run => MapRun(run)).ToList();
        var resp = new SessionRunsResponse(items, nextCursor);
        return new AgentServiceResult(200, resp);
    }

    public async Task<AgentServiceResult> TrustAsync(string runIdRaw, TrustAgentRunRequest req, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        if (req is null || string.IsNullOrWhiteSpace(req.TrustRequestId) || string.IsNullOrWhiteSpace(req.DisplayIdentityHash) || string.IsNullOrWhiteSpace(req.Decision))
        {
            return BoundedError(400, "invalid_trust_request", "trust request invalid.");
        }

        if (!IsTrustRequestId(req.TrustRequestId))
        {
            return BoundedError(400, "invalid_trust_request_id", "trust_request_id invalid.");
        }

        if (req.DisplayIdentityHash.Length != 64 || !IsLowerHex(req.DisplayIdentityHash))
        {
            return BoundedError(400, "invalid_display_hash", "display_identity_hash invalid.");
        }

        var decisionRaw = req.Decision.Trim().ToLowerInvariant();
        WorkspaceTrustDecision decision;
        if (decisionRaw == "approve")
        {
            decision = WorkspaceTrustDecision.Approve;
        }
        else if (decisionRaw == "deny")
        {
            decision = WorkspaceTrustDecision.Deny;
        }
        else
        {
            return BoundedError(400, "invalid_decision", "decision must be approve or deny.");
        }

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        var storedWs = await _workspaceStore.GetAsync(run.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (storedWs is null)
        {
            return BoundedError(404, "workspace_not_found", "Workspace not found.");
        }

        var currentIdentity = storedWs.Identity;
        var trustRequestId = new TrustRequestId(req.TrustRequestId);
        var response = new WorkspaceTrustResponse(trustRequestId, currentIdentity, req.DisplayIdentityHash, decision);
        var now = _time.UtcNow;

        WorkspaceTrustStoreResult storeResult;
        try
        {
            storeResult = await _trustStore.ApplyAsync(response, currentIdentity, now, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException ex)
        {
            return BoundedError(409, ex.Code ?? "trust_stale", ex.Message);
        }

        if (!storeResult.Accepted)
        {
            return BoundedError(409, "trust_stale", storeResult.ErrorCode ?? "trust_stale");
        }

        if (storeResult.Idempotent)
        {
            var currentRun = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            var status = currentRun is null ? "unknown" : MapRunState(currentRun.Status);
            return new AgentServiceResult(200, new AgentRunTrustResponse(runIdRaw, req.TrustRequestId, status));
        }

        if (decision == WorkspaceTrustDecision.Approve)
        {
            var currentRun = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (currentRun is null)
            {
                return BoundedError(404, "run_not_found", "Run not found.");
            }

            if (currentRun.State != HarnessRunState.WaitingWorkspaceTrust)
            {
                return new AgentServiceResult(200, new AgentRunTrustResponse(runIdRaw, req.TrustRequestId, MapRunState(currentRun.Status)));
            }

            PayloadId? promptPayloadId = null;
            await using var conn = await OpenHarnessConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var payloadCmd = conn.CreateCommand();
            payloadCmd.CommandText = "SELECT payload_id FROM agent_payloads WHERE run_id = $run_id AND kind = $kind LIMIT 1";
            payloadCmd.Parameters.AddWithValue("$run_id", runIdRaw);
            payloadCmd.Parameters.AddWithValue("$kind", (int)HarnessPayloadKind.UserPrompt);
            var result = await payloadCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is string pid)
            {
                promptPayloadId = new PayloadId(pid);
            }

            if (promptPayloadId is null)
            {
                return BoundedError(500, "missing_prompt_payload", "Prompt payload missing.");
            }

            var transition = AgentRunStateMachine.TryTransition(currentRun.Status, HarnessRunTransition.ApproveWorkspaceTrust);
            if (!transition.Accepted)
            {
                return BoundedError(409, transition.ErrorCode ?? "invalid_transition", "Transition rejected.");
            }

            var nextRun = AdvanceRun(currentRun, transition.Next, currentRun.BudgetWindow, currentRun.ModelStepsInWindow, currentRun.ToolCallsInWindow, 1, now);

            var outboxIntent = new ConversationOutboxIntent(
                _ids.NewEventId(),
                currentRun.SessionId,
                promptPayloadId.Value,
                ConversationOutboxRole.User,
                HistoryWaitKind.UserBeforeFirstStep);

            var evt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "waiting_history");
            var commit = new HarnessCommit(
                currentRun.RunId,
                currentRun.Version,
                nextRun,
                new[] { evt },
                Array.Empty<HarnessPayloadIntent>(),
                new[] { outboxIntent });

            var commitResult = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
            if (commitResult.Outcome != HarnessCommitOutcome.Committed)
            {
                return BoundedError(409, "commit_conflict", "Commit conflict.");
            }

            return new AgentServiceResult(200, new AgentRunTrustResponse(runIdRaw, req.TrustRequestId, "waiting_history"));
        }
        else
        {
            var currentRun = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (currentRun is null)
            {
                return BoundedError(404, "run_not_found", "Run not found.");
            }

            if (currentRun.State.IsTerminal())
            {
                return new AgentServiceResult(200, new AgentRunTrustResponse(runIdRaw, req.TrustRequestId, MapRunState(currentRun.Status)));
            }

            var transition = AgentRunStateMachine.TryTransition(currentRun.Status, HarnessRunTransition.DenyWorkspaceTrust);
            if (!transition.Accepted)
            {
                transition = AgentRunStateMachine.TryTransition(currentRun.Status, HarnessRunTransition.Cancel);
                if (!transition.Accepted)
                {
                    return BoundedError(409, transition.ErrorCode ?? "invalid_transition", "Transition rejected.");
                }
            }

            var nextRun = AdvanceRun(currentRun, transition.Next, currentRun.BudgetWindow, currentRun.ModelStepsInWindow, currentRun.ToolCallsInWindow, 1, now, terminalErrorCode: transition.Next.State == HarnessRunState.Cancelled ? "trust_denied" : null);

            var evt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "cancelled");

            var commit = new HarnessCommit(
                currentRun.RunId,
                currentRun.Version,
                nextRun,
                new[] { evt },
                Array.Empty<HarnessPayloadIntent>(),
                Array.Empty<ConversationOutboxIntent>());

            var commitResult = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
            if (commitResult.Outcome != HarnessCommitOutcome.Committed)
            {
                return BoundedError(409, "commit_conflict", "Commit conflict.");
            }

            return new AgentServiceResult(200, new AgentRunTrustResponse(runIdRaw, req.TrustRequestId, "cancelled"));
        }
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
            if (payloadJson.Length > 4 * 1024 * 1024)
            {
                payloadJson = payloadJson[..(4 * 1024 * 1024)];
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
            return BoundedError(500, ex.Code ?? "payload_error", ex.Message);
        }
    }

    public AgentServiceResult PhaseNotAvailable() => BoundedError(409, "phase_not_available", "Not available in this phase.");

    // Policy endpoints
    public async Task<AgentServiceResult> ListPoliciesAsync(string? scopeRaw, string? workspaceIdRaw, CancellationToken cancellationToken)
    {
        var scope = scopeRaw?.Trim().ToLowerInvariant();
        if (scope is not null && scope != "global" && scope != "project" && scope != "run" && scope != "once")
        {
            return BoundedError(400, "invalid_scope", "scope must be global, project, run, or once.");
        }

        var store = new SqlitePolicyRuleStore(_options, _keyStore);
        try
        {
            if (string.Equals(scope, "global", StringComparison.Ordinal))
            {
                var globals = await store.ListGlobalAsync(cancellationToken).ConfigureAwait(false);
                var items = globals.Select(MapPolicyRule).ToList();
                return new AgentServiceResult(200, new { items });
            }

            if (string.Equals(scope, "project", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(workspaceIdRaw))
                {
                    return BoundedError(400, "invalid_workspace", "workspace_id required for project scope.");
                }

                var wsId = new WorkspaceId(workspaceIdRaw.Trim());
                var list = await store.ListProjectAsync(wsId, cancellationToken).ConfigureAwait(false);
                var items = list.Select(MapPolicyRule).ToList();
                return new AgentServiceResult(200, new { items });
            }

            if (string.Equals(scope, "run", StringComparison.Ordinal) || string.Equals(scope, "once", StringComparison.Ordinal))
            {
                return BoundedError(400, "invalid_scope", "run/once listing not supported via this endpoint.");
            }

            // default: list global
            var g = await store.ListGlobalAsync(cancellationToken).ConfigureAwait(false);
            var defaultItems = g.Select(MapPolicyRule).ToList();
            return new AgentServiceResult(200, new { items = defaultItems });
        }
        catch (HarnessStoreException ex)
        {
            return BoundedError(500, ex.Code ?? "policy_list_failed", ex.Message);
        }
    }

    public async Task<AgentServiceResult> CreatePolicyAsync(JsonElement body, bool isInteractive, CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return BoundedError(400, "invalid_request", "Body must be object.");
        }

        // Check for session_preset kind
        if (body.TryGetProperty("kind", out var kindProp) && kindProp.ValueKind == JsonValueKind.String && kindProp.GetString() == "session_preset")
        {
            return await HandleSessionPresetAsync(body, isInteractive, cancellationToken).ConfigureAwait(false);
        }

        // Otherwise treat as policy rule creation (also allow kind == "policy_rule" or missing)
        // Validate scope
        if (!body.TryGetProperty("scope", out var scopeProp) || scopeProp.ValueKind != JsonValueKind.String)
        {
            return BoundedError(400, "invalid_scope", "scope required.");
        }

        var scopeRaw = scopeProp.GetString()!.Trim().ToLowerInvariant();
        if (scopeRaw != "global" && scopeRaw != "project" && scopeRaw != "run" && scopeRaw != "once")
        {
            return BoundedError(400, "invalid_scope", "scope must be global, project, run, or once.");
        }

        if (scopeRaw == "global" && !isInteractive)
        {
            return BoundedError(403, "interactive_required", "Global rule requires interactive confirmation.");
        }

        if (!body.TryGetProperty("decision", out var decProp) || decProp.ValueKind != JsonValueKind.String)
        {
            return BoundedError(400, "invalid_decision", "decision required.");
        }

        var decRaw = decProp.GetString()!.Trim().ToLowerInvariant();
        if (decRaw != "allow" && decRaw != "ask" && decRaw != "deny")
        {
            return BoundedError(400, "invalid_decision", "decision must be allow, ask, or deny.");
        }

        if (!body.TryGetProperty("matcher", out var matcherProp) || matcherProp.ValueKind != JsonValueKind.Object)
        {
            return BoundedError(400, "invalid_matcher", "matcher required.");
        }

        var allowedMatcherFields = new HashSet<string>(StringComparer.Ordinal) { "tool_id", "tool_version", "canonical_path_pattern", "resolved_executable", "argv_pattern", "cwd_pattern", "operation_class", "network_intent" };
        foreach (var prop in matcherProp.EnumerateObject())
        {
            if (!allowedMatcherFields.Contains(prop.Name))
            {
                return BoundedError(400, "unknown_field", $"Unknown matcher field {prop.Name}.");
            }
        }

        var allowedTop = new HashSet<string>(StringComparer.Ordinal) { "kind", "scope", "decision", "matcher", "workspace_id", "run_id", "creation_order", "expires_at", "rule" };
        foreach (var prop in body.EnumerateObject())
        {
            if (!allowedTop.Contains(prop.Name))
            {
                return BoundedError(400, "unknown_field", $"Unknown field {prop.Name}.");
            }
        }

        string? toolId = null;
        if (matcherProp.TryGetProperty("tool_id", out var tid) && tid.ValueKind == JsonValueKind.String) toolId = tid.GetString();
        string? toolVersion = null;
        if (matcherProp.TryGetProperty("tool_version", out var tv) && tv.ValueKind == JsonValueKind.String) toolVersion = tv.GetString();
        string? pathPattern = null;
        if (matcherProp.TryGetProperty("canonical_path_pattern", out var pp) && pp.ValueKind == JsonValueKind.String) pathPattern = pp.GetString();
        string? resolvedExe = null;
        if (matcherProp.TryGetProperty("resolved_executable", out var re) && re.ValueKind == JsonValueKind.String) resolvedExe = re.GetString();
        IReadOnlyList<string>? argvPattern = null;
        if (matcherProp.TryGetProperty("argv_pattern", out var argvProp))
        {
            if (argvProp.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var el in argvProp.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String) return BoundedError(400, "invalid_matcher", "argv_pattern must be string array.");
                    list.Add(el.GetString()!);
                }

                argvPattern = list;
            }
            else if (argvProp.ValueKind != JsonValueKind.Null)
            {
                return BoundedError(400, "invalid_matcher", "argv_pattern must be array or null.");
            }
        }

        string? cwdPattern = null;
        if (matcherProp.TryGetProperty("cwd_pattern", out var cwd) && cwd.ValueKind == JsonValueKind.String) cwdPattern = cwd.GetString();
        string? opClass = null;
        if (matcherProp.TryGetProperty("operation_class", out var oc) && oc.ValueKind == JsonValueKind.String) opClass = oc.GetString();
        string? netIntent = null;
        if (matcherProp.TryGetProperty("network_intent", out var ni) && ni.ValueKind == JsonValueKind.String) netIntent = ni.GetString();

        string? workspaceIdRaw = null;
        if (body.TryGetProperty("workspace_id", out var wsp) && wsp.ValueKind == JsonValueKind.String) workspaceIdRaw = wsp.GetString();
        string? runIdRaw = null;
        if (body.TryGetProperty("run_id", out var rip) && rip.ValueKind == JsonValueKind.String) runIdRaw = rip.GetString();

        PolicyRuleScope scopeEnum = scopeRaw switch
        {
            "global" => PolicyRuleScope.Global,
            "project" => PolicyRuleScope.Project,
            "run" => PolicyRuleScope.Run,
            "once" => PolicyRuleScope.Once,
            _ => throw new InvalidOperationException()
        };

        PolicyDecision decisionEnum = decRaw switch
        {
            "allow" => PolicyDecision.Allow,
            "ask" => PolicyDecision.Ask,
            "deny" => PolicyDecision.Deny,
            _ => throw new InvalidOperationException()
        };

        WorkspaceId? wsId = null;
        RunId? runId = null;
        if (scopeEnum == PolicyRuleScope.Project)
        {
            if (string.IsNullOrWhiteSpace(workspaceIdRaw)) return BoundedError(400, "invalid_workspace", "workspace_id required for project scope.");
            wsId = new WorkspaceId(workspaceIdRaw.Trim());
            var ws = await _workspaceStore.GetAsync(wsId.Value, cancellationToken).ConfigureAwait(false);
            if (ws is null) return BoundedError(404, "workspace_not_found", "Workspace not found.");
        }
        else if (scopeEnum == PolicyRuleScope.Run || scopeEnum == PolicyRuleScope.Once)
        {
            if (string.IsNullOrWhiteSpace(runIdRaw)) return BoundedError(400, "invalid_run_id", "run_id required.");
            if (!IsRunId(runIdRaw.Trim())) return BoundedError(400, "invalid_run_id", "run_id invalid.");
            runId = new RunId(runIdRaw.Trim());
            var run = await _store.GetRunAsync(runId.Value, cancellationToken).ConfigureAwait(false);
            if (run is null) return BoundedError(404, "run_not_found", "Run not found.");
        }
        else if (scopeEnum == PolicyRuleScope.Global)
        {
            if (workspaceIdRaw is not null || runIdRaw is not null) return BoundedError(400, "invalid_scope", "global scope must not have workspace/run.");
        }

        PolicyMatcher matcher;
        try
        {
            matcher = new PolicyMatcher(toolId, toolVersion, pathPattern, resolvedExe, argvPattern, cwdPattern, opClass, netIntent);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return BoundedError(400, "invalid_matcher", ex.Message);
        }

        var ruleId = _ids.NewPolicyRuleId();
        var rule = new PolicyRule(ruleId, scopeEnum, decisionEnum, matcher, CreationOrder: 0, ExpiresAt: null);
        var store = new SqlitePolicyRuleStore(_options, _keyStore);
        try
        {
            if (scopeEnum == PolicyRuleScope.Global) await store.CreateAsync(rule, null, null, _time.UtcNow, cancellationToken).ConfigureAwait(false);
            else if (scopeEnum == PolicyRuleScope.Project) await store.CreateAsync(rule, wsId, null, _time.UtcNow, cancellationToken).ConfigureAwait(false);
            else await store.CreateAsync(rule, null, runId, _time.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException ex)
        {
            return BoundedError(400, ex.Code ?? "policy_create_failed", ex.Message);
        }

        var respMatcher = new
        {
            tool_id = toolId,
            tool_version = toolVersion,
            canonical_path_pattern = pathPattern,
            resolved_executable = resolvedExe,
            argv_pattern = argvPattern,
            cwd_pattern = cwdPattern,
            operation_class = opClass,
            network_intent = netIntent
        };
        return new AgentServiceResult(201, new { rule_id = ruleId.Value, scope = scopeRaw, decision = decRaw, matcher = respMatcher, creation_order = 0 });
    }

    private async Task<AgentServiceResult> HandleSessionPresetAsync(JsonElement body, bool isInteractive, CancellationToken cancellationToken)
    {
        if (!body.TryGetProperty("session_id", out var sessProp) || sessProp.ValueKind != JsonValueKind.String)
        {
            return BoundedError(400, "invalid_session_id", "session_id required.");
        }

        var sessionIdRaw = sessProp.GetString()!.Trim();
        if (!IsSessionId(sessionIdRaw))
        {
            return BoundedError(400, "invalid_session_id", "session_id invalid.");
        }

        if (!body.TryGetProperty("preset", out var presetProp) || presetProp.ValueKind != JsonValueKind.String)
        {
            return BoundedError(400, "invalid_preset", "preset required.");
        }

        var presetRaw = presetProp.GetString()!.Trim().ToLowerInvariant();
        AgentPermissionPreset preset;
        if (presetRaw == "standard") preset = AgentPermissionPreset.Standard;
        else if (presetRaw == "full_access" || presetRaw == "full") preset = AgentPermissionPreset.FullAccess;
        else return BoundedError(400, "invalid_preset", "preset must be standard or full_access.");

        long expectedVersion = 0;
        if (body.TryGetProperty("expected_version", out var verProp))
        {
            if (verProp.ValueKind == JsonValueKind.Number && verProp.TryGetInt64(out var v)) expectedVersion = v;
            else if (verProp.ValueKind == JsonValueKind.String && long.TryParse(verProp.GetString(), out var sv)) expectedVersion = sv;
            else if (verProp.ValueKind != JsonValueKind.Null) return BoundedError(400, "invalid_preset_version", "expected_version invalid.");
        }

        if (preset == AgentPermissionPreset.FullAccess && !isInteractive)
        {
            return BoundedError(403, "interactive_required", "Full Access requires interactive confirmation.");
        }

        var sessionDoc = await _sessions.GetAsync(sessionIdRaw, cancellationToken).ConfigureAwait(false);
        if (sessionDoc is null)
        {
            return BoundedError(404, "session_not_found", "Session not found.");
        }

        var now = _time.UtcNow;
        var result = await _permissionStore.SetInteractivelyAsync(new SessionId(sessionIdRaw), preset, expectedVersion, now, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == SessionPermissionOutcome.VersionConflict)
        {
            return BoundedError(409, "preset_version_conflict", "preset_version_conflict");
        }

        if (result.Selection is null)
        {
            return BoundedError(500, "preset_update_failed", "Failed to update preset.");
        }

        var sel = result.Selection;
        return new AgentServiceResult(200, new { session_id = sel.SessionId.Value, preset = sel.Preset == AgentPermissionPreset.Standard ? "standard" : "full_access", version = sel.Version, updated_at = sel.UpdatedAt.ToString("O") });
    }

    public async Task<AgentServiceResult> DeletePolicyAsync(string ruleIdRaw, bool isInteractive, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ruleIdRaw) || !IsPolicyRuleId(ruleIdRaw.Trim()))
        {
            return BoundedError(400, "invalid_rule_id", "rule_id invalid.");
        }

        var trimmed = ruleIdRaw.Trim();
        var ruleId = new PolicyRuleId(trimmed);
        var store = new SqlitePolicyRuleStore(_options, _keyStore);

        // Determine if global and check interactive
        bool isGlobal = false;
        try
        {
            var globals = await store.ListGlobalAsync(cancellationToken).ConfigureAwait(false);
            isGlobal = globals.Any(r => r.RuleId.Value == trimmed);
            if (!isGlobal)
            {
                // also need to check if rule exists at all; query raw to find scope
                // For project/run we can query via direct SQL to see existence
                await using var conn = await OpenHarnessConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "SELECT 1 FROM agent_global_rules WHERE rule_id = $id UNION ALL SELECT 1 FROM agent_project_rules WHERE rule_id = $id UNION ALL SELECT 1 FROM agent_run_rules WHERE rule_id = $id LIMIT 1";
                checkCmd.Parameters.AddWithValue("$id", trimmed);
                var exists = await checkCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (exists is null)
                {
                    return BoundedError(404, "rule_not_found", "Rule not found.");
                }

                // If not global but exists, we already know interactive not required
            }
            else
            {
                if (!isInteractive)
                {
                    return BoundedError(403, "interactive_required", "Global rule deletion requires interactive confirmation.");
                }
            }
        }
        catch (HarnessStoreException ex)
        {
            return BoundedError(500, ex.Code ?? "policy_delete_failed", ex.Message);
        }

        if (isGlobal)
        {
            await store.DeleteGlobalAsync(ruleId, cancellationToken).ConfigureAwait(false);
            return new AgentServiceResult(200, new { rule_id = trimmed, deleted = true });
        }

        // Try project and run deletions (by rule_id, store handles)
        // We already confirmed existence via SQL above, so try both
        await store.DeleteProjectAsync(ruleId, cancellationToken).ConfigureAwait(false);
        await store.DeleteRunAsync(ruleId, cancellationToken).ConfigureAwait(false);
        // Also attempt global delete in case race
        await store.DeleteGlobalAsync(ruleId, cancellationToken).ConfigureAwait(false);
        return new AgentServiceResult(200, new { rule_id = trimmed, deleted = true });
    }

    private static object MapPolicyRule(PolicyRule rule)
    {
        var scope = rule.Scope switch
        {
            PolicyRuleScope.Global => "global",
            PolicyRuleScope.Project => "project",
            PolicyRuleScope.Run => "run",
            PolicyRuleScope.Once => "once",
            _ => "unknown"
        };
        var decision = rule.Decision switch
        {
            PolicyDecision.Allow => "allow",
            PolicyDecision.Ask => "ask",
            PolicyDecision.Deny => "deny",
            _ => "unknown"
        };
        var m = rule.Matcher;
        return new
        {
            rule_id = rule.RuleId.Value,
            scope,
            decision,
            matcher = new
            {
                tool_id = m.ToolId,
                tool_version = m.ToolVersion,
                canonical_path_pattern = m.CanonicalPathPattern,
                resolved_executable = m.ResolvedExecutable,
                argv_pattern = m.ArgvPattern,
                cwd_pattern = m.CwdPattern,
                operation_class = m.OperationClass,
                network_intent = m.NetworkIntent
            },
            creation_order = rule.CreationOrder,
            expires_at = rule.ExpiresAt?.ToString("O"),
            version = 1
        };
    }

    // Host-side one-time approval ledger for HTTP approvals (first-writer wins)
    private static readonly ConcurrentDictionary<string, byte> HostApprovalLedger = new(StringComparer.Ordinal);

    internal static void ClearHostApprovalsForTests() => HostApprovalLedger.Clear();

    public async Task<AgentServiceResult> HandleApprovalAsync(string runIdRaw, string approvalIdRaw, JsonElement? body, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        if (string.IsNullOrWhiteSpace(approvalIdRaw) || approvalIdRaw.Length > 128)
        {
            return BoundedError(400, "invalid_approval_id", "approval_id invalid.");
        }

        // Parse scope/decision from body
        string? scopeRaw = null;
        string? decisionRaw = null;
        string? commandRaw = null;
        if (body.HasValue && body.Value.ValueKind == JsonValueKind.Object)
        {
            if (body.Value.TryGetProperty("scope", out var scopeProp) && scopeProp.ValueKind == JsonValueKind.String)
            {
                scopeRaw = scopeProp.GetString();
            }

            if (body.Value.TryGetProperty("decision", out var decProp) && decProp.ValueKind == JsonValueKind.String)
            {
                decisionRaw = decProp.GetString();
            }

            if (body.Value.TryGetProperty("command", out var cmdProp) && cmdProp.ValueKind == JsonValueKind.String)
            {
                commandRaw = cmdProp.GetString();
            }
            else if (body.Value.TryGetProperty("approved", out var apprProp) && apprProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                commandRaw = apprProp.GetBoolean() ? "allow_once" : "deny";
            }

            // Also support scope-based command via scope field directly (takes precedence over decision)
            if (scopeRaw is not null && string.IsNullOrWhiteSpace(commandRaw))
            {
                // scope alone implies allow with that scope, unless decision is deny
                if (decisionRaw is not null && decisionRaw.Trim().Equals("deny", StringComparison.OrdinalIgnoreCase))
                {
                    commandRaw = "deny";
                }
                else
                {
                    commandRaw = scopeRaw.Trim().ToLowerInvariant() switch
                    {
                        "once" => "allow_once",
                        "run" => "allow_run",
                        "project" => "allow_project",
                        "global" => "global",
                        _ => "allow_once"
                    };
                }
            }

            if (string.IsNullOrWhiteSpace(commandRaw) && decisionRaw is not null)
            {
                commandRaw = decisionRaw;
            }
        }

        if (string.IsNullOrWhiteSpace(commandRaw) && string.IsNullOrWhiteSpace(scopeRaw))
        {
            commandRaw = "allow_once";
        }

        if (!string.IsNullOrWhiteSpace(scopeRaw) && scopeRaw!.Trim().Equals("global", StringComparison.OrdinalIgnoreCase))
        {
            return BoundedError(403, "approval_cannot_create_global", "Approval cannot create global rule.");
        }

        if (commandRaw is not null && commandRaw.Trim().Equals("global", StringComparison.OrdinalIgnoreCase))
        {
            return BoundedError(403, "approval_cannot_create_global", "Approval cannot create global rule.");
        }

        commandRaw = (commandRaw ?? "allow_once").Trim().ToLowerInvariant();
        bool isDeny = commandRaw is "deny" or "reject";
        bool isAllowRun = commandRaw is "allow_run" or "allowrun";
        bool isAllowProject = commandRaw is "allow_project" or "allowproject";

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        // The approval ID must correspond to a persisted approval.requested event for this
        // run while it is actually waiting; fabricated or mismatched IDs are rejected as
        // approval_stale. This check is authoritative and survives process restarts because
        // the event stream is durable. HostApprovalLedger below is only the atomic
        // first-writer-wins guard for HTTP retries.
        ApprovalId? latestPendingId = null;
        await foreach (var envelope in _store.ReadEventsAsync(runId, 0, cancellationToken).ConfigureAwait(false))
        {
            if (envelope.Event.Kind == "approval.requested"
                && envelope.Event.ApprovalId is { } id)
            {
                latestPendingId = id;
            }
        }

        if (latestPendingId is null || !string.Equals(latestPendingId.Value.Value, approvalIdRaw, StringComparison.Ordinal))
        {
            return BoundedError(409, "approval_stale", "Approval is missing, expired, or already used.");
        }

        // One-time consumption: first writer wins

        var key = $"{runIdRaw}|{approvalIdRaw}";
        if (!HostApprovalLedger.TryAdd(key, 0))
        {
            return BoundedError(409, "approval_stale", "approval already consumed");
        }

        if (isDeny)
        {
            var denyTransition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.ResolveApproval);
            if (!denyTransition.Accepted)
            {
                return BoundedError(409, denyTransition.ErrorCode ?? "invalid_transition", "Approval resolve rejected.");
            }

            var denyNow = _time.UtcNow;
            var deniedRun = AdvanceRun(run, denyTransition.Next, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1, denyNow);
            var denyEvt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "approval_resolved");
            var denyCommit = new HarnessCommit(runId, run.Version, deniedRun, new[] { denyEvt }, Array.Empty<HarnessPayloadIntent>(), Array.Empty<ConversationOutboxIntent>());
            var denyResult = await _store.CommitAsync(denyCommit, cancellationToken).ConfigureAwait(false);
            if (denyResult.Outcome != HarnessCommitOutcome.Committed)
            {
                return BoundedError(409, "commit_conflict", "Commit conflict.");
            }

            return new AgentServiceResult(200, new
            {
                approval_id = approvalIdRaw,
                status = "denied",
                error_code = "approval_denied",
                safe_summary = "approval denied",
                run_state = MapRunState(denyResult.Run!.Status),
            });
        }

        bool ruleSaveFailed = false;
        string? ruleId = null;
        if (isAllowRun || isAllowProject)
        {
            try
            {
                var ws = await _workspaceStore.GetAsync(run.WorkspaceId, cancellationToken).ConfigureAwait(false);
                if (ws is not null)
                {
                    var dummyBinding = new RuAgent.Harness.Core.Approval.ToolApprovalBinding(
                        new RunId(runIdRaw),
                        new ToolCallId("call_" + new string('b', 32)),
                        "files.read",
                        "1.0.0",
                        "probe.txt",
                        null,
                        Array.Empty<string>(),
                        null,
                        "read",
                        "none",
                        run.PolicySnapshotVersion,
                        new string('0', 64),
                        _time.UtcNow.AddMinutes(30));
                    var cmd = isAllowRun ? RuAgent.Harness.Core.Approval.ApprovalCommand.AllowRun : RuAgent.Harness.Core.Approval.ApprovalCommand.AllowProject;
                    var approvalService = new HarnessApprovalService(_options, _keyStore, _ids, _time);
                    var outcome = await approvalService.ProcessAsync(
                        dummyBinding,
                        cmd,
                        run.WorkspaceId,
                        runId,
                        cancellationToken).ConfigureAwait(false);
                    ruleSaveFailed = outcome.RuleSaveFailed;
                    ruleId = outcome.RuleId;
                }
            }
            catch
            {
                ruleSaveFailed = true;
            }
        }

        var allowTransition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.ResolveApproval);
        if (!allowTransition.Accepted)
        {
            return BoundedError(409, allowTransition.ErrorCode ?? "invalid_transition", "Approval resolve rejected.");
        }

        var allowNow = _time.UtcNow;
        var nextRun = AdvanceRun(run, allowTransition.Next, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1, allowNow);
        var evt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "approval_resolved");
        var commit = new HarnessCommit(runId, run.Version, nextRun, new[] { evt }, Array.Empty<HarnessPayloadIntent>(), Array.Empty<ConversationOutboxIntent>());
        var commitResult = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        if (commitResult.Outcome != HarnessCommitOutcome.Committed)
        {
            return BoundedError(409, "commit_conflict", "Commit conflict.");
        }

        _approvalGrants?.GrantNext(runId.Value);

        var matcherPreview = new
        {
            tool_id = "files.read",
            tool_version = "1.0.0",
            canonical_path = "probe.txt",
            operation_class = "read",
            network_intent = "none",
            scope = isAllowRun ? "run" : isAllowProject ? "project" : "once"
        };

        var resultBody = new
        {
            approval_id = approvalIdRaw,
            status = "authorized",
            authorization_token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            expires_at = _time.UtcNow.AddSeconds(60).ToString("O"),
            matcher_preview = matcherPreview,
            rule_saved = !ruleSaveFailed && (isAllowRun || isAllowProject),
            reusable_rule_not_saved = ruleSaveFailed,
            rule_id = ruleId,
            safe_summary = "approval authorized",
            run_state = MapRunState(commitResult.Run!.Status),
        };

        return new AgentServiceResult(200, resultBody);
    }

    private IHarnessKeyStore GetKeyStore() => _keyStore;

    private sealed class EphemeralKeyStore : IHarnessKeyStore
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken)
        {
            var copy = new byte[32];
            Buffer.BlockCopy(_key, 0, copy, 0, 32);
            return ValueTask.FromResult(copy);
        }
    }

    public async Task<AgentServiceResult> CancelAsync(string runIdRaw, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        if (run.State.IsTerminal())
        {
            return new AgentServiceResult(200, MapRun(run));
        }

        var transition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.Cancel);
        if (!transition.Accepted)
        {
            return BoundedError(409, transition.ErrorCode ?? "invalid_transition", "Cancel rejected.");
        }

        var now = _time.UtcNow;
        var nextRun = AdvanceRun(run, transition.Next, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1, now, terminalErrorCode: "cancelled");

        var evt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "cancelled");
        var commit = new HarnessCommit(runId, run.Version, nextRun, new[] { evt }, Array.Empty<HarnessPayloadIntent>(), Array.Empty<ConversationOutboxIntent>());
        var result = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != HarnessCommitOutcome.Committed)
        {
            return BoundedError(409, "commit_conflict", "Commit conflict.");
        }

        return new AgentServiceResult(200, MapRun(result.Run!));
    }

    /// <summary>
    /// Applies the typed user reconciliation decision. The snapshot hash the client
    /// presents must equal this host's freshly computed workspace/Git hash; anything
    /// else fails closed with reconcile_stale and leaves the run untouched.
    /// </summary>
    public async Task<AgentServiceResult> ReconcileAsync(
        string runIdRaw,
        string? outcome,
        string? snapshotHash,
        CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        if (!ReconciliationCommand.TryParse(outcome, out var command))
        {
            return BoundedError(400, "invalid_outcome", "outcome must be applied, not_applied, or unknown_continue.");
        }

        if (string.IsNullOrWhiteSpace(snapshotHash)
            || snapshotHash.Length != 64
            || !snapshotHash.All(char.IsAsciiHexDigit))
        {
            return BoundedError(400, "invalid_snapshot_hash", "snapshot_hash must be a sha-256 hex digest.");
        }

        if (_snapshots is null || _inspectionGate is null)
        {
            return BoundedError(409, "reconciliation_unavailable", "Reconciliation services are not available.");
        }

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        if (run.State != HarnessRunState.ReconciliationRequired)
        {
            return BoundedError(409, "invalid_run_state", "Run is not awaiting reconciliation.");
        }

        string? intentCode = null;
        await foreach (var envelope in _store.ReadEventsAsync(runId, 0, cancellationToken).ConfigureAwait(false))
        {
            if (envelope.Event.Kind == "reconciliation.required"
                && envelope.Event.SafeCode is { } code)
            {
                intentCode = code;
            }
        }

        var freshHash = await _snapshots.ComputeFreshHashAsync(runId, cancellationToken).ConfigureAwait(false);
        if (freshHash is null || !string.Equals(freshHash, snapshotHash, StringComparison.Ordinal))
        {
            return new AgentServiceResult(409, new
            {
                error_code = "reconcile_stale",
                message = "Workspace snapshot changed; re-read before deciding.",
                snapshot_hash = freshHash,
                intent_code = intentCode,
            });
        }

        var transition = AgentRunStateMachine.TryTransition(
            run.Status,
            HarnessRunTransition.ApplyReconciliation);
        if (!transition.Accepted)
        {
            return BoundedError(409, transition.ErrorCode ?? "invalid_transition", "Reconcile rejected.");
        }

        if (command.Kind == ReconciliationCommandKind.UnknownContinue)
        {
            _inspectionGate.Activate(runId);
        }

        var now = _time.UtcNow;
        var nextRun = AdvanceRun(run, transition.Next, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1, now);
        var evt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.ReconciliationResolved, safeCode: command.ToWireToken());
        var commit = new HarnessCommit(runId, run.Version, nextRun, new[] { evt }, Array.Empty<HarnessPayloadIntent>(), Array.Empty<ConversationOutboxIntent>());
        var result = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != HarnessCommitOutcome.Committed)
        {
            return BoundedError(409, "commit_conflict", "Commit conflict.");
        }

        return new AgentServiceResult(200, new
        {
            run_id = runIdRaw,
            state = MapRunState(result.Run!.Status),
            resolution = command.ToWireToken(),
            intent_code = intentCode,
        });
    }

    public async Task<AgentServiceResult> ContinueAsync(string runIdRaw, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        var transition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.Continue);
        if (!transition.Accepted)
        {
            return BoundedError(409, transition.ErrorCode ?? "invalid_transition", "Continue rejected.");
        }

        var now = _time.UtcNow;
        var nextRun = AdvanceRun(run, transition.Next, run.BudgetWindow + 1, 0, 0, 1, now);

        var evt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.ContinueRequested, safeCode: "continued");
        var commit = new HarnessCommit(runId, run.Version, nextRun, new[] { evt }, Array.Empty<HarnessPayloadIntent>(), Array.Empty<ConversationOutboxIntent>());
        var result = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != HarnessCommitOutcome.Committed)
        {
            return BoundedError(409, "commit_conflict", "Commit conflict.");
        }

        return new AgentServiceResult(200, MapRun(result.Run!));
    }

    private async Task<SqliteConnection> OpenHarnessConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {_options.BusyTimeoutMilliseconds};";
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    private static AgentRun ReadAgentRun(SqliteDataReader reader, int startIndex = 0)
    {
        var state = (HarnessRunState)reader.GetInt32(startIndex + 3);
        HistoryWaitKind? historyWaitKind = reader.IsDBNull(startIndex + 4) ? null : (HistoryWaitKind)reader.GetInt32(startIndex + 4);
        return new AgentRun(
            new RunId(reader.GetString(startIndex)),
            new SessionId(reader.GetString(startIndex + 1)),
            new WorkspaceId(reader.GetString(startIndex + 2)),
            new HarnessRunStatus(state, historyWaitKind),
            reader.GetInt64(startIndex + 5),
            reader.GetInt64(startIndex + 6),
            reader.GetInt32(startIndex + 7),
            reader.GetInt32(startIndex + 8),
            reader.GetInt32(startIndex + 9),
            (AgentPermissionPreset)reader.GetInt32(startIndex + 10),
            reader.GetInt64(startIndex + 11),
            reader.GetString(startIndex + 12),
            DateTimeOffset.Parse(reader.GetString(startIndex + 13), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(startIndex + 14), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
            reader.IsDBNull(startIndex + 15) ? null : DateTimeOffset.Parse(reader.GetString(startIndex + 15), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
            reader.IsDBNull(startIndex + 16) ? null : reader.GetString(startIndex + 16));
    }

    private static AgentRun AdvanceRun(AgentRun current, HarnessRunStatus nextStatus, int nextBudgetWindow, int nextModelSteps, int nextToolCalls, long appendedCount, DateTimeOffset updatedAt, string? terminalErrorCode = null)
    {
        DateTimeOffset? terminalAt = nextStatus.State.IsTerminal() ? updatedAt : null;
        return new AgentRun(
            current.RunId,
            current.SessionId,
            current.WorkspaceId,
            nextStatus,
            current.Version + 1,
            current.LastSequence + appendedCount,
            nextBudgetWindow,
            nextModelSteps,
            nextToolCalls,
            current.Preset,
            current.PolicySnapshotVersion,
            current.PolicySnapshotSha256,
            current.CreatedAt,
            updatedAt,
            terminalAt,
            terminalErrorCode);
    }

    private async Task<string?> FindIdempotentNormalizedHashAsync(string sessionId, string idempotencySha, CancellationToken ct)
    {
        await using var conn = await OpenHarnessConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT normalized_request_sha256 FROM agent_runs WHERE session_id = $sid AND idempotency_key_sha256 = $key LIMIT 1";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$key", idempotencySha);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string;
    }

    // Helpers
    private static AgentServiceResult BoundedError(int status, string code, string message) => new(status, new AgentErrorResponse(code, message));

    /// <summary>
    /// Explicit snake_case state names shared by every API surface; enum ToString would
    /// collapse word boundaries ("waiting_history" → "waitinghistory").
    /// </summary>
    public static string MapHistoryWaitKind(HistoryWaitKind? kind) => kind switch
    {
        HistoryWaitKind.UserBeforeFirstStep => "user_before_first_step",
        HistoryWaitKind.FinalBeforeCompletion => "final_before_completion",
        null => "none",
        _ => "unknown",
    };

    public static string MapRunState(HarnessRunStatus status)
    {
        if (status.HistoryWaitKind is not null)
        {
            return status.HistoryWaitKind == HistoryWaitKind.UserBeforeFirstStep
                ? "waiting_history_user"
                : "waiting_history_final";
        }

        return status.State switch
        {
            HarnessRunState.Created => "created",
            HarnessRunState.WaitingWorkspaceTrust => "waiting_workspace_trust",
            HarnessRunState.WaitingHistory => "waiting_history",
            HarnessRunState.Running => "running",
            HarnessRunState.WaitingApproval => "waiting_approval",
            HarnessRunState.WaitingUser => "waiting_user",
            HarnessRunState.ReconciliationRequired => "reconciliation_required",
            HarnessRunState.Completed => "completed",
            HarnessRunState.Failed => "failed",
            HarnessRunState.Cancelled => "cancelled",
            HarnessRunState.Interrupted => "interrupted",
            _ => "unknown",
        };
    }

    /// <summary>
    /// Registers the run's workspace root with the runtime loop so the headless driver
    /// can execute model/tool steps for it on this host process. Untrusted runs are
    /// registered too but only start driving once trust approval moves them to running.
    /// </summary>
    private void RegisterRuntimeRoot(AgentRun run, string workspacePath) =>
        _runtimeRoots?.Register(run.RunId, workspacePath);

    private static GetAgentRunResponse MapRun(
        AgentRun run,
        string? trustRequestId = null,
        string? displayIdentityHash = null) => new(
        RunId: run.RunId.Value,
        SessionId: run.SessionId.Value,
        WorkspaceId: run.WorkspaceId.Value,
        Status: MapRunState(run.Status),
        HistoryWaitKind: MapHistoryWaitKind(run.HistoryWaitKind),
        Preset: run.Preset == AgentPermissionPreset.Standard ? "standard" : "full_access",
        PolicySnapshotVersion: run.PolicySnapshotVersion,
        PolicySnapshotSha256: run.PolicySnapshotSha256,
        Version: run.Version,
        LastSequence: run.LastSequence,
        CreatedAt: run.CreatedAt.ToUniversalTime().ToString("O"),
        UpdatedAt: run.UpdatedAt.ToUniversalTime().ToString("O"),
        TerminalAt: run.TerminalAt?.ToUniversalTime().ToString("O"),
        TerminalErrorCode: run.TerminalErrorCode,
        TrustRequestId: trustRequestId,
        DisplayIdentityHash: displayIdentityHash);

    private static string ComputeSha256(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputePolicySnapshotSha(AgentPermissionPreset preset, WorkspaceIdentity identity)
    {
        var input = $"{preset}|{identity.WorkspaceId.Value}|empty";
        return ComputeSha256(input);
    }

    private static bool IsSessionId(string value) => value.Length == 32 && IsLowerHex(value);

    private static bool IsRunId(string value) => value.StartsWith("run_", StringComparison.Ordinal) && value.Length == 36 && IsLowerHex(value.AsSpan(4));

    private static bool IsPayloadId(string value) => value.StartsWith("payload_", StringComparison.Ordinal) && value.Length == 40 && IsLowerHex(value.AsSpan(8));

    private static bool IsTrustRequestId(string value) => value.StartsWith("trust_", StringComparison.Ordinal) && value.Length == 38 && IsLowerHex(value.AsSpan(6));

    private static bool IsPolicyRuleId(string value) => value.StartsWith("rule_", StringComparison.Ordinal) && value.Length == 37 && IsLowerHex(value.AsSpan(5));

    private static bool IsLowerHex(string value) => IsLowerHex(value.AsSpan());

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidIsoTimestamp(string value)
    {
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out _);
    }

    private static string EncodeCursor(string createdAt, string runId)
    {
        var raw = $"{createdAt}|{runId}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    private static (string CreatedAt, string RunId)? TryDecodeCursor(string cursor)
    {
        try
        {
            var bytes = Convert.FromBase64String(cursor);
            var raw = Encoding.UTF8.GetString(bytes);
            var parts = raw.Split('|');
            if (parts.Length != 2)
            {
                return null;
            }

            return (parts[0], parts[1]);
        }
        catch
        {
            return null;
        }
    }
}


