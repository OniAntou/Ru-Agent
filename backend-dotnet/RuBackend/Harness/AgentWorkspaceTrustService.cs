using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Approval;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Conversation;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Workspace;
using RuBackend.Providers;
using RuAgent.Runtime.Adapters.Models;
using static RuBackend.Harness.AgentV1Shared;

namespace RuBackend.Harness;

public sealed class AgentWorkspaceTrustService
{
    private readonly IHarnessEventStore _store;
    private readonly SqliteWorkspaceStore _workspaceStore;
    private readonly WorkspaceTrustRequestStore _trustStore;
    private readonly WorkspaceTrustService _trustService;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly AgentV1SubagentGuard _subagentGuard;
    private readonly HarnessSqliteOptions _options;
    private readonly ILogger<AgentWorkspaceTrustService>? _logger;

    public AgentWorkspaceTrustService(IHarnessEventStore store,
        SqliteWorkspaceStore workspaceStore, WorkspaceTrustRequestStore trustStore,
        WorkspaceTrustService trustService, IIdGenerator ids, ITimeProvider time,
        AgentV1SubagentGuard subagentGuard, HarnessSqliteOptions options,
        ILogger<AgentWorkspaceTrustService>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        _trustStore = trustStore ?? throw new ArgumentNullException(nameof(trustStore));
        _trustService = trustService ?? throw new ArgumentNullException(nameof(trustService));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _subagentGuard = subagentGuard ?? throw new ArgumentNullException(nameof(subagentGuard));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public async Task<(string? TrustRequestId, string? DisplayIdentityHash)> ResolveTrustChallengeAsync(
        AgentRun run,
        CancellationToken cancellationToken)
    {
        if (run.State != HarnessRunState.WaitingWorkspaceTrust)
        {
            return (null, null);
        }

        var registration = await _workspaceStore.GetAsync(run.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (registration is null)
        {
            return (null, null);
        }

        return (registration.TrustRequest.RequestId.Value, registration.Identity.DisplayIdentityHash);
    }

    public async Task<AgentServiceResult> TrustAsync(string runIdRaw, TrustAgentRunRequest req, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        if (await _subagentGuard.IsInternalRunAsync(new RunId(runIdRaw), cancellationToken).ConfigureAwait(false))
            return BoundedError(409, "subagent_read_only", "Subagents are controlled by the main agent. Send your request to the parent.");

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

        var trustRequestId = new TrustRequestId(req.TrustRequestId);
        if (storedWs.TrustRequest.RequestId != trustRequestId)
        {
            return BoundedError(409, "trust_stale", "trust_stale");
        }

        if (!string.Equals(
                storedWs.TrustRequest.DisplayedIdentityHash,
                req.DisplayIdentityHash,
                StringComparison.Ordinal))
        {
            return BoundedError(409, "trust_stale", "trust_display_hash_mismatch");
        }

        if (run.State != HarnessRunState.WaitingWorkspaceTrust)
        {
            return new AgentServiceResult(200, new AgentRunTrustResponse(runIdRaw, req.TrustRequestId, MapRunState(run.Status)));
        }

        WorkspaceIdentity currentIdentity;
        try
        {
            currentIdentity = await _trustService.RevalidateIdentityAsync(storedWs, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HarnessStoreException ex)
        {
            _logger?.LogWarning(ex, "workspace trust identity revalidation failed");
            return BoundedError(409, ex.Code ?? "trust_stale", ex.Message);
        }

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

            HarnessCommitResult commitResult;
            try
            {
                commitResult = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HarnessStoreException ex)
            {
                _logger?.LogWarning(ex, "workspace trust run transition commit failed");
                return BoundedError(409, "commit_conflict", "Commit conflict.");
            }

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

            if (currentRun.State != HarnessRunState.WaitingWorkspaceTrust)
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

            HarnessCommitResult commitResult;
            try
            {
                commitResult = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HarnessStoreException ex)
            {
                _logger?.LogWarning(ex, "workspace trust run transition commit failed");
                return BoundedError(409, "commit_conflict", "Commit conflict.");
            }

            if (commitResult.Outcome != HarnessCommitOutcome.Committed)
            {
                return BoundedError(409, "commit_conflict", "Commit conflict.");
            }

            return new AgentServiceResult(200, new AgentRunTrustResponse(runIdRaw, req.TrustRequestId, "cancelled"));
        }
    }

    private Task<SqliteConnection> OpenHarnessConnectionAsync(CancellationToken cancellationToken) =>
        _options.OpenConnectionAsync(cancellationToken);
}
