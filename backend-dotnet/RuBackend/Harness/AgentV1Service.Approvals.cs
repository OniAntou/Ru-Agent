using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
using RuBackend.Providers;
using RuAgent.Runtime.Adapters.Models;

namespace RuBackend.Harness;

public sealed partial class AgentV1Service
{
    // Host-side one-time approval ledger for HTTP approvals (first-writer wins).
    // Entries are only a retry guard; the durable approval event/run state remains
    // authoritative. Expire old entries so a host that processes many approvals
    // cannot retain an unbounded amount of memory until restart.
    // Static: the ledger guards concurrent HTTP races across all service instances
    // in this process, and tests clear it via ClearHostApprovalsForTests.
    private static readonly ConcurrentDictionary<string, DateTimeOffset> HostApprovalLedger = new(StringComparer.Ordinal);
    private static readonly TimeSpan HostApprovalLedgerTtl = TimeSpan.FromMinutes(30);

    // The remembered terminal pattern keeps at most this many leading non-flag
    // argv tokens: the subcommand shape ("git status", "npm run build"), never
    // the full argument list the call happened to carry.
    private const int MaxRememberedArgvTokens = 2;

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
        var proposedEvents = new Dictionary<string, HarnessEvent>(StringComparer.Ordinal);
        await foreach (var envelope in _store.ReadEventsAsync(runId, 0, cancellationToken).ConfigureAwait(false))
        {
            if (envelope.Event.Kind == "tool.proposed"
                && envelope.Event.ToolCallId is { } proposedToolCall)
            {
                proposedEvents[proposedToolCall.Value] = envelope.Event;
            }
            else if (envelope.Event.Kind == "approval.requested"
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

        ToolApprovalBinding? approvalBinding = null;
        if (pendingApprovalEvent?.ToolCallId is { } pendingToolCallId
            && proposedEvents.TryGetValue(pendingToolCallId.Value, out var proposedEvent)
            && proposedEvent.PayloadId is { } proposalPayloadId)
        {
            approvalBinding = await BuildApprovalBindingAsync(
                    run,
                    pendingToolCallId,
                    proposalPayloadId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (approvalBinding is null)
        {
            return BoundedError(409, "approval_stale", "Approval binding is missing or invalid.");
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

        // allow_always remembers a bounded pattern as a project-scope allow
        // rule so a later matching call authorizes without asking. The pattern
        // is derived from the reviewed intent, never widened past it: a rule
        // save never widens the current authorization (the Broker token is
        // still single-use and bound to this exact intent), protected paths
        // still deny before rules, and a save failure or a one-time-only
        // reason degrades to allow_once.
        var durableMatcher = BuildAllowAlwaysMatcher(
            approvalBinding,
            pendingApprovalEvent?.SafeCode,
            externalDirectoryRequested);
        string? savedRuleId = null;
        var ruleSaved = false;
        if (isAllowAlways && durableMatcher is not null)
        {
            (ruleSaved, savedRuleId) = await TrySaveAllowAlwaysRuleAsync(
                run, durableMatcher, cancellationToken).ConfigureAwait(false);
        }

        // Pi-style: an allow is a decision about this call. The durable
        // resolution commits first; the exact approved intent is then
        // remembered for the Agent session, and the Broker grant below
        // authorizes the call in flight.
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

        // One allow is remembered for the session: the approved pattern (when
        // allow_always saved one) or the exact intent is materialized into
        // later policy snapshots for this Agent session, so a later matching
        // call evaluates as allowed without another prompt. The decision is
        // durable at this point, and the memory still only ever allows what
        // the Broker can match. External-directory approvals keep the Broker
        // folder grant: the durable rule already covers later runs, so no
        // session entry is added for them.
        if (!externalDirectoryRequested)
        {
            SessionApprovalLedger.Remember(
                run.SessionId.Value,
                isAllowAlways && ruleSaved && durableMatcher is not null
                    ? durableMatcher
                    : BuildApprovalMatcher(approvalBinding),
                _time.UtcNow);
        }

        // The grant is issued only after the durable approval resolution is
        // committed: a commit conflict must not consume a Broker authorization
        // that the Host then reports as stale.
        // The Broker command follows the pending kind, never the caller's
        // wording: external folders become a session grant, everything else
        // stays a one-time token. allow_always bridges as itself so the
        // execution authority binds the same exact intent; the reusable rule
        // is a Host-side project record created above.
        string? brokerAuthorizationToken = null;
        var brokerCommand = externalDirectoryRequested
            ? "allow_session_directory"
            : isAllowAlways ? "allow_always" : "allow_once";
        bool bridged;
        try
        {
            (bridged, brokerAuthorizationToken) = await _brokerGateways
                .RespondApprovalAsync(runIdRaw, approvalIdRaw, brokerCommand, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ToolTransportException)
        {
            bridged = false;
        }

        if (!bridged || string.IsNullOrWhiteSpace(brokerAuthorizationToken))
        {
            HostApprovalLedger.TryRemove(key, out _);
            return BoundedError(
                502,
                "approval_authorization_failed",
                "The approval was recorded but the execution authority did not authorize it.");
        }

        // The Broker grant or one-time token authorizes this call; the Host
        // additionally remembers the approved pattern for the session (and, for
        // allow_always, as the durable project record above). The preview shows
        // the matcher the rule actually carries, so a client can tell the user
        // what "always" remembered.
        var previewMatcher = ruleSaved && durableMatcher is not null
            ? durableMatcher
            : BuildApprovalMatcher(approvalBinding);
        var matcherPreview = new
        {
            tool_id = previewMatcher.ToolId,
            tool_version = previewMatcher.ToolVersion,
            canonical_path = previewMatcher.CanonicalPathPattern,
            resolved_executable = previewMatcher.ResolvedExecutable,
            argv_pattern = previewMatcher.ArgvPattern,
            cwd_pattern = previewMatcher.CwdPattern,
            operation_class = previewMatcher.OperationClass,
            network_intent = previewMatcher.NetworkIntent,
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
    /// Builds the bounded pattern an allow_always decision remembers as a
    /// project-scope allow rule, or null when the ask must stay one-time.
    /// Terminal run calls remember the resolved executable plus at most two
    /// leading non-flag argv tokens in the exact launch directory (the
    /// Claude-style command prefix: "git status", "npm run build"); shell-mode
    /// scripts and flag-first invocations stay exact because a prefix of them
    /// would remember more than the user reviewed. An external-directory ask
    /// remembers the approved folder as a path pattern covering its subtree.
    /// Protected-path and explicit-ask reasons never become a reusable rule:
    /// remembering the first grants standing access to a credential directory,
    /// and an explicit ask outranks any allow rule anyway.
    /// </summary>
    internal static PolicyMatcher? BuildAllowAlwaysMatcher(
        ToolApprovalBinding approvalBinding,
        string? askSafeCode,
        bool externalDirectoryRequested)
    {
        ArgumentNullException.ThrowIfNull(approvalBinding);
        if (!externalDirectoryRequested
            && askSafeCode is "terminal_protected_path_requires_approval" or "rule_asks")
        {
            return null;
        }

        if (externalDirectoryRequested)
        {
            var pattern = BuildExternalDirectoryPattern(approvalBinding.CanonicalPath);
            return pattern is null
                ? null
                : new PolicyMatcher(CanonicalPathPattern: pattern);
        }

        return new PolicyMatcher(
            approvalBinding.ToolId,
            approvalBinding.ToolVersion,
            approvalBinding.CanonicalPath,
            approvalBinding.ResolvedExecutable,
            GeneralizeTerminalArgv(approvalBinding),
            approvalBinding.Cwd,
            approvalBinding.OperationClass,
            approvalBinding.NetworkIntent);
    }

    /// <summary>
    /// A bounded command prefix for a remembered terminal run: keep at most the
    /// first two argv tokens that do not start with '-', which is the
    /// subcommand shape. Nothing usable to keep returns the exact argv.
    /// </summary>
    private static IReadOnlyList<string> GeneralizeTerminalArgv(ToolApprovalBinding approvalBinding)
    {
        var argv = approvalBinding.Argv;
        if (!string.Equals(approvalBinding.ToolId, "bash", StringComparison.Ordinal)
            || IsShellModeInvocation(approvalBinding.ResolvedExecutable, argv))
        {
            return argv;
        }

        var take = 0;
        while (take < argv.Count
            && take < MaxRememberedArgvTokens
            && !argv[take].StartsWith('-'))
        {
            take++;
        }

        return take == 0 ? argv : argv.Take(take).ToArray();
    }

    /// <summary>
    /// Shell mode is persisted as a known shell wrapper around one script
    /// string. Recognizing the shape keeps the remembered rule exact: a prefix
    /// of arbitrary shell text is not a safe pattern.
    /// </summary>
    private static bool IsShellModeInvocation(string? executable, IReadOnlyList<string> argv)
    {
        if (executable is null || argv.Count < 4)
        {
            return false;
        }

        var name = Path.GetFileName(executable);
        var isShell = string.Equals(name, "powershell", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "powershell.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "pwsh", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "pwsh.exe", StringComparison.OrdinalIgnoreCase);
        return isShell
            && string.Equals(argv[0], "-NoProfile", StringComparison.OrdinalIgnoreCase)
            && string.Equals(argv[1], "-NonInteractive", StringComparison.OrdinalIgnoreCase)
            && string.Equals(argv[2], "-Command", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The approved external path as a bounded directory pattern: the nearest
    /// existing ancestor directory (a file target grants its parent), resolved
    /// to its canonical final path so the pattern matches the canonical
    /// candidate the Broker evaluates, plus a subtree suffix. Null when nothing
    /// usable or representable exists: the save then degrades to allow_once.
    /// </summary>
    internal static string? BuildExternalDirectoryPattern(string? approvedPath)
    {
        if (string.IsNullOrWhiteSpace(approvedPath) || approvedPath.Length > ApprovalPathMaxLength)
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(approvedPath);
            var directory = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                directory = Path.GetDirectoryName(directory);
            }

            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            // Same canonicalization the terminal cwd binding uses, so the
            // pattern matches the final-path candidate and cannot be
            // retargeted by an intermediate link.
            var canonical = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(directory);
            // 4096 is the matcher's path-pattern cap; the "\**" suffix must fit.
            if (string.IsNullOrWhiteSpace(canonical) || canonical.Length + 3 > 4096)
            {
                return null;
            }

            canonical = canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return canonical.Length == 0 ? null : canonical + "\\**";
        }
        catch (Exception error) when (error is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or IOException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static PolicyMatcher BuildApprovalMatcher(ToolApprovalBinding approvalBinding) => new(
        approvalBinding.ToolId,
        approvalBinding.ToolVersion,
        approvalBinding.CanonicalPath,
        approvalBinding.ResolvedExecutable,
        approvalBinding.Argv,
        approvalBinding.Cwd,
        approvalBinding.OperationClass,
        approvalBinding.NetworkIntent);

    private async Task<ToolApprovalBinding?> BuildApprovalBindingAsync(
        AgentRun run,
        ToolCallId toolCallId,
        PayloadId payloadId,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await _payloadStore
                .ReadForClientAsync(run.RunId, payloadId, cancellationToken)
                .ConfigureAwait(false);
            var envelope = ToolArgumentsCodec.Deserialize(payload.Plaintext);
            if (!envelope.IsObject || envelope.ToolId is null || envelope.ToolVersion is null || !envelope.HasObjectArguments)
            {
                return null;
            }

            var root = envelope.Root;
            var arguments = envelope.Arguments;
            var toolId = ReadString(root, "tool_id", ApprovalToolIdMaxLength);
            var toolVersion = ReadString(root, "tool_version", ApprovalToolVersionMaxLength);
            if (toolId is null || toolVersion is null)
            {
                return null;
            }

            var op = arguments.TryGetProperty("op", out var opNode) && opNode.ValueKind == JsonValueKind.String
                ? opNode.GetString()
                : null;
            var mode = arguments.TryGetProperty("mode", out var modeNode) && modeNode.ValueKind == JsonValueKind.String
                ? modeNode.GetString()
                : null;
            var isBash = string.Equals(toolId, "bash", StringComparison.Ordinal);
            if (string.Equals(toolId, "bash", StringComparison.Ordinal)
                && !string.Equals(mode, "run", StringComparison.Ordinal)
                && !string.Equals(mode, "shell", StringComparison.Ordinal))
            {
                return null;
            }

            // write still discriminates by op, so it must carry one that maps. read does
            // not any more: list and help are their own tools, so a missing op on read is
            // no longer a malformed call.
            if (string.Equals(toolId, "write", StringComparison.Ordinal) && MapPiOp(toolId, op) is null)
            {
                return null;
            }

            var canonicalPath = ReadString(arguments, "path", ApprovalPathMaxLength);
            var requestedExecutable = ReadString(arguments, "executable", ApprovalExecutableMaxLength);
            var shellRuntime = ReadString(arguments, "shell", 32) ?? "powershell";
            var argv = ReadStringArray(arguments, "arguments");
            var isShellMode = isBash && string.Equals(mode, "shell", StringComparison.Ordinal);
            if (isShellMode)
            {
                if (!string.Equals(shellRuntime, "powershell", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(shellRuntime, "pwsh", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var script = ReadString(arguments, "script", ApprovalScriptMaxLength);
                if (script is null)
                {
                    return null;
                }

                requestedExecutable = shellRuntime;
                argv = ["-NoProfile", "-NonInteractive", "-Command", script];
            }
            else if (isBash
                && string.Equals(mode, "run", StringComparison.Ordinal)
                && requestedExecutable is null)
            {
                return null;
            }

            if ((toolId is "read" or "write" or "grep" or "glob") && MapPiOp(toolId, op) is null)
            {
                return null;
            }

            if (toolId is "read" or "list" or "write" or "edit" or "grep" or "glob"
                && canonicalPath is null
                && !IsTargetFreePiOp(toolId, op))
            {
                return null;
            }

            var cwd = ReadString(arguments, "working_directory", ApprovalCwdMaxLength);
            if (isBash)
            {
                if (_runtimeRoots is null || !_runtimeRoots.TryGetRoot(run.RunId, out var rootPath)
                    || string.IsNullOrWhiteSpace(rootPath))
                {
                    return null;
                }

                var trustedRoot = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(rootPath);
                var requestedCwd = cwd ?? ".";
                var candidateCwd = Path.IsPathRooted(requestedCwd)
                    ? Path.GetFullPath(requestedCwd)
                    : Path.GetFullPath(Path.Combine(trustedRoot, requestedCwd));
                if (!Directory.Exists(candidateCwd))
                {
                    return null;
                }

                // The gateway stores the final directory identity in its policy
                // context. Resolve the same identity here so a reusable approval
                // cannot be silently bound to a lexical path or a retargeted link.
                cwd = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(candidateCwd);
            }

            var operationClass = (toolId, MapPiOp(toolId, op), isShellMode, isBash, mode) switch
            {
                ("read", "list", _, _, _) => "list",
                ("read", "read", _, _, _) => "read",
                ("read", "help", _, _, _) => "read",
                ("grep", _, _, _, _) => "search",
                ("glob", _, _, _, _) => "glob",
                ("web_search", _, _, _, _) => "search",
                ("web_fetch", _, _, _, _) => "fetch",
                ("write", "write", _, _, _) => "write",
                ("write", "delete", _, _, _) => "delete",
                ("write", "restore", _, _, _) => "restore",
                ("edit", _, _, _, _) => "edit",
                (_, _, _, true, _) => "terminal",
                ("files.list", _, _, _, _) => "list",
                ("files.glob", _, _, _, _) => "glob",
                ("files.search", _, _, _, _) => "search",
                ("files.read", _, _, _, _) => "read",
                ("files.write", _, _, _, _) => "write",
                ("files.edit", _, _, _, _) => "edit",
                // Legacy persisted-approval replay only: proposals persisted by
                // builds that still advertised apply_patch must stay approvable
                // after an upgrade. Not model-reachable.
                ("files.apply_patch", _, _, _, _) => "patch",
                ("files.delete", _, _, _, _) => "delete",
                ("files.restore", _, _, _, _) => "restore",
                ("terminal.run", _, _, _, _) => "terminal",
                ("terminal.shell", _, _, _, _) => "terminal",
                _ => "mutation",
            };
            var networkIntent = "none";
            if (arguments.TryGetProperty("network_intent", out var network))
            {
                if (network.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return null;
                }

                networkIntent = network.ValueKind == JsonValueKind.True ? "declared" : "none";
            }
            // Legacy field name kept for replay of proposals persisted by older
            // builds; current proposals declare network_intent.
            else if (arguments.TryGetProperty("network", out var legacyNetwork))
            {
                if (legacyNetwork.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return null;
                }

                networkIntent = legacyNetwork.ValueKind == JsonValueKind.True ? "declared" : "none";
            }
            var intentHash = HarnessIntentCanonicalizer.ComputeHash(
                toolId,
                toolVersion,
                arguments);

            return new ToolApprovalBinding(
                run.RunId,
                toolCallId,
                toolId,
                toolVersion,
                canonicalPath,
                requestedExecutable,
                argv,
                cwd,
                operationClass,
                networkIntent,
                run.PolicySnapshotVersion,
                intentHash,
                _time.UtcNow.AddMinutes(30));
        }
        catch (Exception error) when (error is JsonException
            or InvalidOperationException
            or ArgumentException
            or IOException
            or HarnessStoreException
            or CryptographicException)
        {
            return null;
        }
    }

    private static string? MapPiOp(string toolId, string? op) => (toolId, op) switch
    {
        // read carries no discriminator since the surface split; list and help are
        // first-class tools, so there is no (read, op) pair left to map.
        ("read", _) => "read",
        ("list", _) => "list",
        ("help", _) => "help",
        ("grep", _) => "grep",
        ("glob", _) => "glob",
        ("web_search", _) => "web_search",
        ("web_fetch", _) => "web_fetch",
        ("write", "write") => "write",
        ("write", "delete") => "delete",
        ("write", "restore") => "restore",
        _ => null,
    };

    private static bool IsTargetFreePiOp(string toolId, string? op) =>
        string.Equals(toolId, "help", StringComparison.Ordinal)
        || string.Equals(toolId, "web_search", StringComparison.Ordinal)
        || string.Equals(toolId, "web_fetch", StringComparison.Ordinal);

    private static string? ReadString(JsonElement objectNode, string propertyName, int maxLength)
    {
        if (!objectNode.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"{propertyName} must be a string", propertyName);
        }

        var text = value.GetString();
        if (text is null
            || string.IsNullOrWhiteSpace(text)
            || text.Length > maxLength
            || text.Contains('\0'))
        {
            throw new ArgumentException($"{propertyName} is invalid", propertyName);
        }

        return text;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement objectNode, string propertyName)
    {
        if (!objectNode.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<string>();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("arguments array is invalid", propertyName);
        }

        var values = new List<string>();
        if (value.GetArrayLength() > RuAgent.Protocol.TerminalLimits.MaxArguments)
        {
            throw new ArgumentException("arguments array is too large", propertyName);
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text)
            {
                throw new ArgumentException("arguments array is invalid", propertyName);
            }

            if (text.Length > ApprovalArgumentMaxLength || text.Contains('\0'))
            {
                throw new ArgumentException("arguments array is invalid", propertyName);
            }

            values.Add(text);
        }

        return values;
    }
}
