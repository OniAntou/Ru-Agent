using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Approval;
using RuAgent.Harness.Core.Context;
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
using RuBackend.Harness.Policy;
using RuBackend.Harness.Workspace;
using static RuBackend.Harness.AgentV1Shared;
using RuBackend.Providers;
using RuAgent.Runtime.Adapters.Models;

namespace RuBackend.Harness;

public sealed class AgentApprovalsService
{
    private readonly IHarnessEventStore _store;
    private readonly HarnessSqliteOptions _options;
    private readonly SqliteWorkspaceStore _workspaceStore;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly IHarnessKeyStore _keyStore;
    private readonly Broker.IToolGatewayFactory _brokerGateways;
    private readonly AgentV1SubagentGuard _subagentGuard;

    public AgentApprovalsService(IHarnessEventStore store, HarnessSqliteOptions options,
        SqliteWorkspaceStore workspaceStore, IIdGenerator ids, ITimeProvider time,
        IHarnessKeyStore keyStore, Broker.IToolGatewayFactory brokerGateways,
        AgentV1SubagentGuard subagentGuard)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _brokerGateways = brokerGateways ?? throw new ArgumentNullException(nameof(brokerGateways));
        _subagentGuard = subagentGuard ?? throw new ArgumentNullException(nameof(subagentGuard));
    }

    // Host-side one-time approval ledger for HTTP approvals (first-writer wins).
    // Entries are only a retry guard; the durable approval event/run state remains
    // authoritative. Expire old entries so a host that processes many approvals
    // cannot retain an unbounded amount of memory until restart.
    // Static: the ledger guards concurrent HTTP races across all service instances
    // in this process, and tests clear it via ClearHostApprovalsForTests.
    private static readonly ConcurrentDictionary<string, DateTimeOffset> HostApprovalLedger = new(StringComparer.Ordinal);
    private static readonly TimeSpan HostApprovalLedgerTtl = TimeSpan.FromMinutes(30);

    internal static void ClearHostApprovalsForTests() => HostApprovalLedger.Clear();

    private static void PruneHostApprovalLedger(DateTimeOffset now)
    {
        var cutoff = now - HostApprovalLedgerTtl;
        foreach (var entry in HostApprovalLedger)
        {
            if (entry.Value <= cutoff)
            {
                HostApprovalLedger.TryRemove(new KeyValuePair<string, DateTimeOffset>(entry.Key, entry.Value));
            }
        }
    }

    public async Task<AgentServiceResult> HandleApprovalAsync(string runIdRaw, string approvalIdRaw, JsonElement? body, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        if (await _subagentGuard.IsInternalRunAsync(new RunId(runIdRaw), cancellationToken).ConfigureAwait(false))
            return BoundedError(409, "subagent_read_only", "Subagents are controlled by the main agent. Send your request to the parent.");

        if (string.IsNullOrWhiteSpace(approvalIdRaw) || approvalIdRaw.Length > 128)
        {
            return BoundedError(400, "invalid_approval_id", "approval_id invalid.");
        }

        // Parse command. Pi-style UX: exactly 3 commands — allow_once,
        // allow_session_directory (external folders only), deny — plus
        // allow_always, which authorizes once and remembers the exact intent
        // as a project-scope allow rule (Claude-style "yes, and remember").
        // run/project/global are rejected outright below. Any allow also
        // remembers the exact intent for the Agent session; only allow_always
        // creates a durable project rule.
        string? decisionRaw = null;
        string? commandRaw = null;
        if (body.HasValue && body.Value.ValueKind == JsonValueKind.Object)
        {
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

            if (string.IsNullOrWhiteSpace(commandRaw) && decisionRaw is not null)
            {
                commandRaw = decisionRaw.Trim().Equals("allow", StringComparison.OrdinalIgnoreCase)
                    ? "allow_once"
                    : decisionRaw;
            }
        }

        if (string.IsNullOrWhiteSpace(commandRaw))
        {
            return BoundedError(400, "invalid_approval_command", "Approval command is required.");
        }

        commandRaw = commandRaw.Trim().ToLowerInvariant();
        bool isDeny = commandRaw is "deny" or "reject";
        bool isAllowSessionDirectory = commandRaw is "allow_session_directory" or "allowsessiondirectory";
        var isAllowOnce = commandRaw is "allow_once" or "allowonce" or "once";
        var isAllowAlways = commandRaw is "allow_always" or "allowalways" or "always";
        if (!isDeny && !isAllowOnce && !isAllowSessionDirectory && !isAllowAlways)
        {
            return BoundedError(400, "invalid_approval_command", "Approval command is invalid.");
        }

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        // A durable approval request is consumable only while the run is still
        // waiting for that approval.  Relying solely on an in-memory ledger would
        // allow a replay after a host restart to resolve an already-used request.
        if (run.State != HarnessRunState.WaitingApproval)
        {
            return BoundedError(409, "approval_stale", "Approval is missing, expired, or already used.");
        }

        // The approval ID must correspond to a persisted approval.requested event for this
        // run while it is actually waiting; fabricated or mismatched IDs are rejected as
        // approval_stale. This check is authoritative and survives process restarts because
        // the event stream is durable. HostApprovalLedger below is only the atomic
        // first-writer-wins guard for HTTP retries.
        ApprovalId? latestPendingId = null;
        HarnessEvent? pendingApprovalEvent = null;
        await foreach (var envelope in _store.ReadEventsAsync(runId, 0, cancellationToken).ConfigureAwait(false))
        {
            if (envelope.Event.Kind == "approval.requested"
                && envelope.Event.ApprovalId is { } id)
            {
                latestPendingId = id;
                pendingApprovalEvent = envelope.Event;
            }
        }

        if (latestPendingId is null || !string.Equals(latestPendingId.Value.Value, approvalIdRaw, StringComparison.Ordinal))
        {
            return BoundedError(409, "approval_stale", "Approval is missing, expired, or already used.");
        }

        if (pendingApprovalEvent is null
            || ApprovalState.IsExpired(pendingApprovalEvent.OccurredAt, _time.UtcNow))
        {
            return BoundedError(409, "approval_stale", "Approval is missing, expired, or already used.");
        }

        // Before anything commits, an allow must still be a pending approval the Broker holds. The Host
        // answers 409 approval_stale for an approval the Broker no longer knows or has let expire, rather than
        // committing a resolution the execution authority would then fail to authorize (502). The same
        // non-consuming read says whether allow_always may remember the tool class. Deny is not gated: it
        // commits the denial and then makes a best-effort Broker cleanup, so a run whose Broker approval is
        // gone can still be ended. An unconfigured authority is refused below with 503 instead.
        if (!isDeny && _brokerGateways is { IsConfigured: true })
        {
            var probe = await _brokerGateways.ProbeApprovalAsync(runIdRaw, approvalIdRaw, cancellationToken).ConfigureAwait(false);
            if (!probe.Known)
            {
                return BoundedError(409, "approval_stale", "Approval binding is missing or invalid.");
            }

            if (isAllowAlways && !probe.Rememberable)
            {
                return BoundedError(400, "invalid_approval_command", "This mutation requires a one-time approval.");
            }
        }

        // allow_session_directory is only meaningful for external folders;
        // terminal risk stays a one-time token. Anything else fails closed.
        if (isAllowSessionDirectory
            && !string.Equals(pendingApprovalEvent?.SafeCode, "external_directory_required", StringComparison.Ordinal))
        {
            return BoundedError(400, "invalid_approval_command", "Approval command is not supported for this approval.");
        }

        // One-time consumption: first writer wins. The durable checks above prevent
        // a replay after restart; this ledger only closes the concurrent HTTP race.

        var key = $"{runIdRaw}|{approvalIdRaw}";
        var ledgerNow = _time.UtcNow;
        PruneHostApprovalLedger(ledgerNow);
        if (!HostApprovalLedger.TryAdd(key, ledgerNow))
        {
            return BoundedError(409, "approval_stale", "approval already consumed");
        }

        if (isDeny)
        {
            var denyTransition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.Fail);
            if (!denyTransition.Accepted)
            {
                HostApprovalLedger.TryRemove(key, out _);
                return BoundedError(409, denyTransition.ErrorCode ?? "invalid_transition", "Approval resolve rejected.");
            }

            var denyNow = _time.UtcNow;
            var deniedRun = AdvanceRun(
                run,
                denyTransition.Next,
                run.BudgetWindow,
                run.ModelStepsInWindow,
                run.ToolCallsInWindow,
                1,
                denyNow,
                terminalErrorCode: "approval_denied");
            var denyEvt = new HarnessEventIntent(
                _ids.NewEventId(),
                HarnessEventKind.RunFailed,
                safeCode: "approval_denied");
            var denyCommit = new HarnessCommit(runId, run.Version, deniedRun, new[] { denyEvt }, Array.Empty<HarnessPayloadIntent>(), Array.Empty<ConversationOutboxIntent>());
            HarnessCommitResult denyResult;
            try
            {
                denyResult = await _store.CommitAsync(denyCommit, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                HostApprovalLedger.TryRemove(key, out _);
                throw;
            }
            if (denyResult.Outcome != HarnessCommitOutcome.Committed)
            {
                HostApprovalLedger.TryRemove(key, out _);
                return BoundedError(409, "commit_conflict", "Commit conflict.");
            }

            // Best-effort cross-system cleanup: the Broker pending approval expires
            // on its own, so a failed cleanup must not fail the durable denial.
            if (_brokerGateways is { IsConfigured: true })
            {
                try
                {
                    await _brokerGateways.RespondApprovalAsync(
                        runIdRaw,
                        approvalIdRaw,
                        "deny",
                        cancellationToken).ConfigureAwait(false);
                }
                catch (ToolTransportException)
                {
                }
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

        var externalDirectoryRequested = string.Equals(
            pendingApprovalEvent?.SafeCode,
            "external_directory_required",
            StringComparison.Ordinal);
        if (_brokerGateways is not { IsConfigured: true })
        {
            // An approval is a request for the execution authority to permit a call.
            // With no authority to ask, there is nothing to authorize and the call
            // cannot run, so the Host must say so. Answering "authorized" with a token
            // it invented would report a permission that nothing issued.
            HostApprovalLedger.TryRemove(key, out _);
            return BoundedError(
                503,
                "broker_gateway_unavailable",
                "Approval requires the execution authority, which is not configured.");
        }

        // Pi-style: an allow is a decision about this call. The durable resolution commits first; the Broker
        // grant then authorizes the call in flight, and only after that is a reusable rule saved or the intent
        // remembered for the session.
        var allowTransition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.ResolveApproval);
        if (!allowTransition.Accepted)
        {
            HostApprovalLedger.TryRemove(key, out _);
            return BoundedError(409, allowTransition.ErrorCode ?? "invalid_transition", "Approval resolve rejected.");
        }

        var allowNow = _time.UtcNow;
        var nextRun = AdvanceRun(run, allowTransition.Next, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1, allowNow);
        var evt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "approval_resolved");
        var commit = new HarnessCommit(runId, run.Version, nextRun, new[] { evt }, Array.Empty<HarnessPayloadIntent>(), Array.Empty<ConversationOutboxIntent>());
        HarnessCommitResult commitResult;
        try
        {
            commitResult = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            HostApprovalLedger.TryRemove(key, out _);
            throw;
        }
        if (commitResult.Outcome != HarnessCommitOutcome.Committed)
        {
            HostApprovalLedger.TryRemove(key, out _);
            return BoundedError(409, "commit_conflict", "Commit conflict.");
        }

        // The grant is issued only after the durable approval resolution is committed: a commit conflict must
        // not consume a Broker authorization that the Host then reports as stale. The Broker command follows the
        // pending kind, never the caller's wording: external folders become a session grant, everything else
        // stays a one-time token. The remember plan comes back from this same consuming call, so the Host saves
        // and remembers only what the Broker decided may be remembered.
        var brokerCommand = externalDirectoryRequested
            ? "allow_session_directory"
            : isAllowAlways ? "allow_always" : "allow_once";
        bool bridged;
        string? brokerAuthorizationToken;
        RuAgent.Broker.Policy.ApprovalRememberPlan? rememberPlan;
        try
        {
            (bridged, brokerAuthorizationToken, rememberPlan) = await _brokerGateways
                .RespondApprovalAsync(runIdRaw, approvalIdRaw, brokerCommand, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ToolTransportException)
        {
            bridged = false;
            brokerAuthorizationToken = null;
            rememberPlan = null;
        }

        if (!bridged || string.IsNullOrWhiteSpace(brokerAuthorizationToken))
        {
            HostApprovalLedger.TryRemove(key, out _);
            return BoundedError(
                502,
                "approval_authorization_failed",
                "The approval was recorded but the execution authority did not authorize it.");
        }

        // The Broker grant authorizes this call. Only now is a reusable rule saved, and only for allow_always
        // with the durable pattern the Broker built; a save failure, or a call the Broker would not remember,
        // degrades to allow_once.
        var plan = rememberPlan ?? RuAgent.Broker.Policy.ApprovalRememberPlan.None;
        var durableMatcher = plan.DurableMatcher is { } durable ? ToHostMatcher(durable) : null;
        string? savedRuleId = null;
        var ruleSaved = false;
        if (isAllowAlways && durableMatcher is not null)
        {
            (ruleSaved, savedRuleId) = await TrySaveAllowAlwaysRuleAsync(
                run, durableMatcher, cancellationToken).ConfigureAwait(false);
        }

        // The matcher this approval remembers: the saved durable pattern when one was stored, otherwise the exact
        // intent the Broker plan carries. External-directory approvals add no session entry, because the durable
        // folder rule already covers later runs.
        var rememberedMatcher = ruleSaved && durableMatcher is not null
            ? durableMatcher
            : plan.SessionMatcher is { } sessionIntent ? ToHostMatcher(sessionIntent) : null;
        if (!externalDirectoryRequested && rememberedMatcher is not null)
        {
            SessionApprovalLedger.Remember(run.SessionId.Value, rememberedMatcher, _time.UtcNow);
        }

        // The preview shows the matcher the rule actually carries, so a client can tell the user what "always"
        // remembered.
        var matcherPreview = rememberedMatcher is null ? null : new
        {
            tool_id = rememberedMatcher.ToolId,
            tool_version = rememberedMatcher.ToolVersion,
            canonical_path = rememberedMatcher.CanonicalPathPattern,
            resolved_executable = rememberedMatcher.ResolvedExecutable,
            argv_pattern = rememberedMatcher.ArgvPattern,
            cwd_pattern = rememberedMatcher.CwdPattern,
            operation_class = rememberedMatcher.OperationClass,
            network_intent = rememberedMatcher.NetworkIntent,
            scope = ruleSaved ? "project" : "once"
        };

        var resultBody = new
        {
            approval_id = approvalIdRaw,
            status = "authorized",
            authorization_token = brokerAuthorizationToken,
            expires_at = _time.UtcNow.AddSeconds(60).ToString("O"),
            matcher_preview = matcherPreview,
            rule_saved = ruleSaved,
            reusable_rule_not_saved = !ruleSaved,
            rule_id = savedRuleId,
            safe_summary = "approval authorized",
            run_state = MapRunState(commitResult.Run!.Status),
        };

        return new AgentServiceResult(200, resultBody);
    }

    private async Task<(bool Saved, string? RuleId)> TrySaveAllowAlwaysRuleAsync(
        AgentRun run,
        PolicyMatcher matcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        try
        {
            var wsId = new WorkspaceId(run.WorkspaceId.Value);
            var ws = await _workspaceStore.GetAsync(wsId, cancellationToken).ConfigureAwait(false);
            if (ws is null)
            {
                return (false, null);
            }

            var store = new SqlitePolicyRuleStore(_options, _keyStore);
            var existing = await store.ListProjectAsync(wsId, cancellationToken).ConfigureAwait(false);
            var creationOrder = existing.Count == 0 ? 0 : existing.Max(r => r.CreationOrder) + 1;

            var rule = new PolicyRule(
                _ids.NewPolicyRuleId(),
                PolicyRuleScope.Project,
                PolicyDecision.Allow,
                matcher,
                creationOrder,
                null);
            await store.CreateAsync(rule, wsId, null, _time.UtcNow, cancellationToken).ConfigureAwait(false);
            return (true, rule.RuleId.Value);
        }
        catch (Exception ex) when (ex is ArgumentException
            or ArgumentOutOfRangeException
            or InvalidOperationException
            or HarnessStoreException
            or IOException)
        {
            return (false, null);
        }
    }

    /// <summary>
    /// The Host's stored form of a Broker-built matcher. The Broker derives every field; the Host copies them.
    /// </summary>
    private static PolicyMatcher ToHostMatcher(BrokerPolicyMatcherWire matcher) => new(
        matcher.ToolId,
        matcher.ToolVersion,
        matcher.CanonicalPathPattern,
        matcher.ResolvedExecutable,
        matcher.ArgvPattern,
        matcher.CwdPattern,
        matcher.OperationClass,
        matcher.NetworkIntent);
}
