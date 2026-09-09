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
using RuBackend.Agent;
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
        // allow_session_directory (external folders only), deny.
        // run/project/global are rejected outright below; approvals never
        // create reusable rules.
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
        if (!isDeny && !isAllowOnce && !isAllowSessionDirectory)
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
            || _time.UtcNow - pendingApprovalEvent.OccurredAt > TimeSpan.FromMinutes(30))
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

        string? brokerAuthorizationToken = null;
        var externalDirectoryRequested = string.Equals(
            pendingApprovalEvent?.SafeCode,
            "external_directory_required",
            StringComparison.Ordinal);
        // The Broker command follows the pending kind, never the caller's
        // wording: external folders become a session grant, everything else
        // stays a one-time token.
        if (_brokerGateways is { IsConfigured: true })
        {
            var brokerCommand = externalDirectoryRequested
                ? "allow_session_directory"
                : "allow_once";
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

            if (!bridged)
            {
                HostApprovalLedger.TryRemove(key, out _);
                return BoundedError(
                    409,
                    "approval_stale",
                    "Approval could not be authorized by the execution authority.");
            }
        }

        // Pi-style: approvals never create reusable rules. The Broker grant or
        // one-time token is the whole authorization.
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

        // The Broker grant or one-time token is the whole authorization; the
        // Host keeps no reusable rule and no shadow grant ledger.
        var matcherPreview = new
        {
            tool_id = approvalBinding.ToolId,
            tool_version = approvalBinding.ToolVersion,
            canonical_path = approvalBinding.CanonicalPath,
            resolved_executable = approvalBinding.ResolvedExecutable,
            argv_pattern = approvalBinding.Argv,
            cwd_pattern = approvalBinding.Cwd,
            operation_class = approvalBinding.OperationClass,
            network_intent = approvalBinding.NetworkIntent,
            scope = "once"
        };

        var resultBody = new
        {
            approval_id = approvalIdRaw,
            status = "authorized",
            authorization_token = brokerAuthorizationToken
                ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            expires_at = _time.UtcNow.AddSeconds(60).ToString("O"),
            matcher_preview = matcherPreview,
            rule_saved = false,
            reusable_rule_not_saved = false,
            rule_id = (string?)null,
            safe_summary = "approval authorized",
            run_state = MapRunState(commitResult.Run!.Status),
        };

        return new AgentServiceResult(200, resultBody);
    }

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
            using var document = JsonDocument.Parse(payload.Plaintext);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tool_id", out var toolIdNode)
                || toolIdNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("tool_version", out var versionNode)
                || versionNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("arguments", out var arguments)
                || arguments.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

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

            if ((string.Equals(toolId, "read", StringComparison.Ordinal)
                    || string.Equals(toolId, "write", StringComparison.Ordinal))
                && op is null)
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

            if ((toolId is "read" or "write") && MapPiOp(toolId, op) is null)
            {
                return null;
            }

            if (toolId is "read" or "write" or "edit"
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
                ("read", "glob", _, _, _) => "glob",
                ("read", "search", _, _, _) => "search",
                ("read", "read", _, _, _) => "read",
                ("read", "help", _, _, _) => "read",
                ("read", "web_search", _, _, _) => "read",
                ("read", "web_fetch", _, _, _) => "read",
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

    private string? TryGetExternalDirectory(
        AgentRun run,
        ToolApprovalBinding binding,
        bool externalDirectoryRequested)
    {
        if (!externalDirectoryRequested)
        {
            return null;
        }

        if (_runtimeRoots is null
            || !_runtimeRoots.TryGetRoot(run.RunId, out var rootPath)
            || string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        try
        {
            var trustedRoot = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(rootPath);
            string? candidate;
            if (binding.OperationClass == "terminal")
            {
                candidate = binding.Cwd;
            }
            else if (binding.CanonicalPath is not null)
            {
                candidate = Path.IsPathRooted(binding.CanonicalPath)
                    ? Path.GetFullPath(binding.CanonicalPath)
                    : Path.GetFullPath(Path.Combine(trustedRoot, binding.CanonicalPath));
            }
            else
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            string directory;
            if (Directory.Exists(candidate))
            {
                directory = candidate;
            }
            else
            {
                var parent = Directory.GetParent(candidate)?.FullName;
                if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                {
                    return null;
                }

                directory = parent;
            }

            var canonicalDirectory = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(directory);
            if (IsWithinPath(canonicalDirectory, trustedRoot)
                || !ExternalDirectoryPolicy.TryCreatePattern(canonicalDirectory, out _))
            {
                return null;
            }

            return canonicalDirectory;
        }
        catch (Exception error) when (error is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? MapPiOp(string toolId, string? op) => (toolId, op) switch
    {
        ("read", "list") => "list",
        ("read", "glob") => "glob",
        ("read", "search") => "search",
        ("read", "read") => "read",
        ("read", "help") => "help",
        ("read", "web_search") => "web_search",
        ("read", "web_fetch") => "web_fetch",
        ("write", "write") => "write",
        ("write", "delete") => "delete",
        ("write", "restore") => "restore",
        _ => null,
    };

    private static bool IsTargetFreePiOp(string toolId, string? op) =>
        string.Equals(toolId, "read", StringComparison.Ordinal)
        && op is "help" or "web_search" or "web_fetch";

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
