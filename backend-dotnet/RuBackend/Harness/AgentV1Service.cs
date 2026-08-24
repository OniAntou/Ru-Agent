using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Protocol.Harness;
using RuBackend.Business.Sessions;
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
    }

    // Run creation
    public async Task<AgentServiceResult> CreateRunAsync(CreateAgentRunRequest request, CancellationToken cancellationToken)
    {
        // Validate bounded inputs
        if (request is null)
        {
            return BoundedError(400, "invalid_request", "Request body required.");
        }

        var sessionIdRaw = request.SessionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sessionIdRaw) || sessionIdRaw.Length > 64)
        {
            return BoundedError(400, "invalid_session_id", "session_id invalid.");
        }

        // Session id must be 32 lower hex for harness, but also allow business pattern? Enforce harness strict 32 hex
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

        // Verify session exists via ISessionStore
        var sessionDoc = await _sessions.GetAsync(sessionIdRaw, cancellationToken).ConfigureAwait(false);
        if (sessionDoc is null)
        {
            return BoundedError(404, "session_not_found", "Session not found.");
        }

        // Determine workspace identity
        WorkspaceIdentity workspaceIdentity;
        try
        {
            workspaceIdentity = await _resolver.ResolveAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return BoundedError(400, "invalid_workspace", "workspace_path invalid.");
        }

        // Ensure workspace registered (creates if not exists)
        try
        {
            var trustReq = await _trustService.EnsureTrustRequestAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            // Use stored workspace identity from trustReq for consistency (should match workspaceIdentity)
            // But if resolver returned different due to race, re-resolve
            var storedWs = await _workspaceStore.GetAsync(workspaceIdentity.WorkspaceId, cancellationToken).ConfigureAwait(false);
            if (storedWs is not null)
            {
                workspaceIdentity = storedWs.Identity;
            }
        }
        catch (Exception)
        {
            return BoundedError(400, "invalid_workspace", "workspace registration failed.");
        }

        // Preset/version handling per spec, with idempotency-aware replay allowance
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
                // Allow idempotent replay without version if same key (any hash). Let store handle idempotency_conflict.
                var idemShaCheck = ComputeSha256(idempotencyKey);
                var storedHash = await FindIdempotentNormalizedHashAsync(sessionIdRaw, idemShaCheck, cancellationToken).ConfigureAwait(false);
                if (storedHash is not null)
                {
                    // Same key exists – treat as replay attempt, use stored version for hash and let store decide AlreadyExists vs idempotency_conflict
                    effectiveVersion = existingSelection.Version;
                }
                else if (preset == AgentPermissionPreset.Standard && existingSelection.Preset == AgentPermissionPreset.Standard)
                {
                    // Lenient: allow omitted version for subsequent Standard runs that match stored preset, using stored version.
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

        // Check active workspace conflict before store call? store will handle but we map error.
        var isTrusted = await _workspaceStore.IsTrustedAsync(workspaceIdentity, preset, cancellationToken).ConfigureAwait(false);

        var now = _time.UtcNow;
        var runId = _ids.NewRunId();
        var initialState = isTrusted
            ? new HarnessRunStatus(HarnessRunState.WaitingHistory, HistoryWaitKind.UserBeforeFirstStep)
            : new HarnessRunStatus(HarnessRunState.WaitingWorkspaceTrust);

        // Build run projection
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

        // Prepare events/payloads/outbox
        var events = new List<HarnessEventIntent>
        {
            new(_ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: isTrusted ? "waiting_history" : "waiting_workspace_trust"),
        };
        // Sequence must match LastSequence, so count must be run.LastSequence after creation? But run.LastSequence is 0, ValidateNewRun requires Events.Count == LastSequence
        // So we need to set run.LastSequence to events.Count at construction, i.e., adjust before creation.
        // Recreate run with correct lastSequence
        var initialEventCount = isTrusted ? 2 : 1;
        // For trusted, we have additional waiting_history event duplicate? Actually HarnessRunService had 1 then added second if trusted plus outbox.
        // Simplify: trusted has 1 event for waiting_history, untrusted has 1 for waiting_workspace_trust.
        // But we need to ensure ValidateNewRun passes: run.LastSequence == events.Count
        // So recreate with lastSequence = events.Count
        // For trusted we actually want to have 1 event? Let's keep 1 for both to satisfy invariant; the second outbox-related transition will be merged?
        // For trusted, the run is in WaitingHistory, only one event needed.
        // We'll keep events count as 1.
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
            // Need to adjust events/outbox relationship: outbox requires payload in same write set – satisfied.
            // Also ValidateOutboxBoundary requires run state WaitingHistory with that historyWaitKind – satisfied.
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
            var resp = MapRun(storeResult.Run);
            return new AgentServiceResult(201, resp);
        }

        if (storeResult.Outcome == CreateRunOutcome.AlreadyExists)
        {
            var resp = MapRun(storeResult.Run);
            return new AgentServiceResult(200, resp);
        }

        // Conflict
        var code = storeResult.ErrorCode ?? "conflict";
        // Map to required HTTP codes and error_code strings
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

        return new AgentServiceResult(200, MapRun(run));
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

        // Validate cursor if present
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

            // Verify anchor belongs to this session and exists; if not, reject without leaking
            var anchorRun = await _store.GetRunAsync(new RunId(anchorRunId!), cancellationToken).ConfigureAwait(false);
            if (anchorRun is null || anchorRun.SessionId.Value != sessionIdRaw)
            {
                return BoundedError(400, "invalid_cursor", "cursor invalid.");
            }

            // Also ensure anchor is not foreign: if session mismatch already rejected.
        }

        // Query runs
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

        var items = runs.Select(MapRun).ToList();
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

        // Resolve current workspace identity (must match stored)
        // Use resolver to get current identity for path? We don't have path; use stored identity as current
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

        // If idempotent, just return success without re-committing run
        if (storeResult.Idempotent)
        {
            // Run may already have been advanced; just return current run status
            var currentRun = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            var status = currentRun?.State.ToString().ToLowerInvariant() ?? "unknown";
            return new AgentServiceResult(200, new AgentRunTrustResponse(runIdRaw, req.TrustRequestId, status));
        }

        // Need to advance run based on decision
        if (decision == WorkspaceTrustDecision.Approve)
        {
            // Only if run is in WaitingWorkspaceTrust, transition to WaitingHistory with outbox
            var currentRun = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (currentRun is null)
            {
                return BoundedError(404, "run_not_found", "Run not found.");
            }

            if (currentRun.State != HarnessRunState.WaitingWorkspaceTrust)
            {
                // Already transitioned; treat as success
                return new AgentServiceResult(200, new AgentRunTrustResponse(runIdRaw, req.TrustRequestId, currentRun.State.ToString().ToLowerInvariant()));
            }

            // Need to find UserPrompt payload id for this run
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

            // Build commit to WaitingHistory
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
            // Deny: transition to Cancelled
            var currentRun = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (currentRun is null)
            {
                return BoundedError(404, "run_not_found", "Run not found.");
            }

            if (currentRun.State.IsTerminal())
            {
                return new AgentServiceResult(200, new AgentRunTrustResponse(runIdRaw, req.TrustRequestId, currentRun.State.ToString().ToLowerInvariant()));
            }

            if (currentRun.State != HarnessRunState.WaitingWorkspaceTrust)
            {
                // If not waiting trust, deny still cancels if non-terminal
            }

            var transition = AgentRunStateMachine.TryTransition(currentRun.Status, HarnessRunTransition.DenyWorkspaceTrust);
            if (!transition.Accepted)
            {
                // Fallback to Cancel
                transition = AgentRunStateMachine.TryTransition(currentRun.Status, HarnessRunTransition.Cancel);
                if (!transition.Accepted)
                {
                    return BoundedError(409, transition.ErrorCode ?? "invalid_transition", "Transition rejected.");
                }
            }

            var nextRun = AdvanceRun(currentRun, transition.Next, currentRun.BudgetWindow, currentRun.ModelStepsInWindow, currentRun.ToolCallsInWindow, 1, now, terminalErrorCode: transition.Next.State == HarnessRunState.Cancelled ? "trust_denied" : null);

            var evt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "cancelled");

            // For cancelled, need terminal_at? Advance handles via terminal_at = now inside AgentRun constructor if state terminal
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

        // Verify binding exists and not RecoveryOpaque before decrypting
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
            // Truncate if needed to bounded
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
        var nextRun = AdvanceRun(run, transition.Next, run.BudgetWindow + 1, 0, 0, 1, now); // BudgetWindow increment?
        // Actually AgentBudgetPolicy.Continue would open new window; we simulate by incrementing window and resetting counts
        // Use coordinator logic? For simplicity use direct.

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

    private static GetAgentRunResponse MapRun(AgentRun run) => new(
        RunId: run.RunId.Value,
        SessionId: run.SessionId.Value,
        WorkspaceId: run.WorkspaceId.Value,
        Status: run.State.ToString().ToLowerInvariant(),
        HistoryWaitKind: run.HistoryWaitKind?.ToString().ToLowerInvariant(),
        Preset: run.Preset == AgentPermissionPreset.Standard ? "standard" : "full_access",
        PolicySnapshotVersion: run.PolicySnapshotVersion,
        PolicySnapshotSha256: run.PolicySnapshotSha256,
        Version: run.Version,
        LastSequence: run.LastSequence,
        CreatedAt: run.CreatedAt.ToUniversalTime().ToString("O"),
        UpdatedAt: run.UpdatedAt.ToUniversalTime().ToString("O"),
        TerminalAt: run.TerminalAt?.ToUniversalTime().ToString("O"),
        TerminalErrorCode: run.TerminalErrorCode);

    private static string ComputeSha256(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputePolicySnapshotSha(AgentPermissionPreset preset, WorkspaceIdentity identity)
    {
        // Simplified deterministic snapshot hash: hash of preset+workspaceId+empty rules
        var input = $"{preset}|{identity.WorkspaceId.Value}|empty";
        return ComputeSha256(input);
    }

    private static bool IsSessionId(string value) => value.Length == 32 && IsLowerHex(value);

    private static bool IsRunId(string value) => value.StartsWith("run_", StringComparison.Ordinal) && value.Length == 36 && IsLowerHex(value.AsSpan(4));

    private static bool IsPayloadId(string value) => value.StartsWith("payload_", StringComparison.Ordinal) && value.Length == 40 && IsLowerHex(value.AsSpan(8));

    private static bool IsTrustRequestId(string value) => value.StartsWith("trust_", StringComparison.Ordinal) && value.Length == 38 && IsLowerHex(value.AsSpan(6));

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
