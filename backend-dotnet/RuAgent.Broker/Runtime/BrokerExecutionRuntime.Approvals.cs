using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Protocol;

namespace RuAgent.Broker.Runtime;

public sealed partial class BrokerExecutionRuntime
{
    /// <summary>
    /// v2 <c>approval.respond</c>. The session precondition is checked before the
    /// lifecycle gate (as on the wire), then the decision is validated against
    /// the pending binding, consumed exactly once, and answered with a 60s
    /// authorization token when approved. The adapter never invokes an adapter
    /// here; <c>approval_result</c> is strictly authorization.
    /// </summary>
    public Task<ApprovalDecisionResult> RespondApprovalAsync(AgentEnvelope request, CancellationToken cancellationToken)
    {
        if (!_session.Owns(request.SessionId))
        {
            return Task.FromResult(new ApprovalDecisionResult(
                new RuntimeError("broker_not_ready", "session is not open"),
                ToolResultStatuses.Denied,
                SafeSummary: string.Empty));
        }

        return UnderLifecycleAsync(() => RespondApprovalCore(request), cancellationToken);
    }

    private ApprovalDecisionResult RespondApprovalCore(AgentEnvelope request)
    {
        var payload = request.Payload;

        // approval.respond without a run lease stays reserved: there is no
        // pending v2 approval to consume on a session-only request.
        var runId = PayloadReads.OptionalString(payload, "run_id");
        if (string.IsNullOrWhiteSpace(runId))
        {
            // For v1 sessions without run lease, approval.respond is still phase_not_available
            return ApprovalDecisionResult.Denied("phase_not_available", "v2 approval decisions are not wired without run_id");
        }

        var approvalId = PayloadReads.OptionalString(payload, "approval_id");
        if (string.IsNullOrWhiteSpace(approvalId))
        {
            return ApprovalDecisionResult.Denied("invalid_approval_id", "missing approval_id");
        }

        // Parse command: prefer "command", fallback to "approved" bool
        ApprovalCommandV2 command;
        var commandRaw = PayloadReads.OptionalString(payload, "command");
        if (!string.IsNullOrWhiteSpace(commandRaw))
        {
            var normalizedCommand = commandRaw.ToLowerInvariant();
            if (normalizedCommand is not ("allow_once" or "allowonce"
                or "allow_session_directory" or "allowsessiondirectory"
                or "allow_always" or "allowalways"
                or "deny"))
            {
                return ApprovalDecisionResult.Denied("invalid_command", "unknown approval command");
            }

            command = normalizedCommand switch
            {
                "allow_once" or "allowonce" => ApprovalCommandV2.AllowOnce,
                "allow_session_directory" or "allowsessiondirectory" => ApprovalCommandV2.AllowSessionDirectory,
                "allow_always" or "allowalways" => ApprovalCommandV2.AllowAlways,
                "deny" => ApprovalCommandV2.Deny,
                _ => throw new InvalidOperationException("invalid_command"),
            };
        }
        else if (payload["approved"] is JsonValue av && av.TryGetValue<bool>(out var approved))
        {
            command = approved ? ApprovalCommandV2.AllowOnce : ApprovalCommandV2.Deny;
        }
        else
        {
            return ApprovalDecisionResult.Denied("invalid_command", "missing command");
        }

        if (!TryGetOwnedRunLease(request, runId!, out var lease, out var leaseError) || lease is null)
        {
            return ApprovalDecisionResult.Denied(leaseError, "run lease is not open");
        }

        if (!_pendingV2.TryPeekV2(approvalId!, out var pending) || pending is null)
        {
            return ApprovalDecisionResult.Denied("approval_not_found", "approval not found");
        }

        if (!string.Equals(pending.Binding.RunId, runId, StringComparison.Ordinal))
        {
            // Approval identifiers are opaque, but the binding is still scoped to
            // the run that requested it. Never let a caller consume another run's
            // pending approval by guessing or replaying its id.
            return ApprovalDecisionResult.Denied("approval_stale", "approval is bound to a different run");
        }

        if (pending.Binding.ApprovalKind == "subagent_apply"
            && command is ApprovalCommandV2.AllowSessionDirectory or ApprovalCommandV2.AllowAlways)
            return ApprovalDecisionResult.Denied("invalid_command", "result batches require a single-use approval");

        if (command == ApprovalCommandV2.AllowSessionDirectory)
        {
            // Directory grants are only available for external-directory approvals.
            // Terminal and web approvals stay one-time tokens and never create
            // reusable grants.
            if (!string.Equals(pending.Binding.ApprovalKind, "external_directory", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(pending.Binding.BoundaryDirectory))
            {
                return ApprovalDecisionResult.Denied("invalid_command", "directory grant is not available for this approval");
            }
        }

        // A policy snapshot bump between the request and the decision invalidates
        // every pending allow: the decision must be re-evaluated against the new
        // snapshot instead of authorizing an intent the current rules never saw.
        if ((command is ApprovalCommandV2.AllowOnce or ApprovalCommandV2.AllowSessionDirectory or ApprovalCommandV2.AllowAlways)
            && lease.Snapshot.Version != pending.Binding.SnapshotVersion)
        {
            return ApprovalDecisionResult.Denied("approval_stale", "approval is bound to an older snapshot");
        }

        // Build expected binding: by default use pending's binding. The optional
        // override fields exist so a test can drive the stale path; they are a
        // test seam only and are compiled out of Release, where caller-supplied
        // fields can never influence what the approval is compared against.
        if (_delegations.TryGetValue(runId!, out var scope) && !TryGetDelegatingParent(scope, out _))
            return ApprovalDecisionResult.Denied("subagent_parent_revoked", "delegation is no longer active");

        var expectedBinding = pending.Binding;
#if DEBUG
        var overrideHash = PayloadReads.OptionalString(payload, "expected_intent_hash");
        var overrideVersionRaw = payload["expected_snapshot_version"] is JsonValue sv && sv.TryGetValue<long>(out var ov) ? (long?)ov : null;
        var overrideToolId = PayloadReads.OptionalString(payload, "expected_tool_id");
        if (overrideHash is not null || overrideVersionRaw is not null || overrideToolId is not null)
        {
            expectedBinding = new V2ApprovalBinding(
                RunId: pending.Binding.RunId,
                ToolCallId: pending.Binding.ToolCallId,
                ToolId: overrideToolId ?? pending.Binding.ToolId,
                ToolVersion: pending.Binding.ToolVersion,
                CanonicalPath: pending.Binding.CanonicalPath,
                ResolvedExecutable: pending.Binding.ResolvedExecutable,
                Argv: pending.Binding.Argv,
                Cwd: pending.Binding.Cwd,
                OperationClass: pending.Binding.OperationClass,
                NetworkIntent: pending.Binding.NetworkIntent,
                SnapshotVersion: overrideVersionRaw ?? pending.Binding.SnapshotVersion,
                IntentHash: overrideHash ?? pending.Binding.IntentHash);
        }
#endif

        var now = DateTimeOffset.UtcNow;
        var resolution = _pendingV2.TryConsumeV2(approvalId!, command, expectedBinding, now);

        switch (resolution.Outcome)
        {
            case V2ApprovalOutcome.Expired:
                return ApprovalDecisionResult.Denied("approval_expired", "approval expired");
            case V2ApprovalOutcome.Stale:
                return ApprovalDecisionResult.Denied("approval_stale", "approval stale (intent changed)");
            case V2ApprovalOutcome.NotFound:
                return ApprovalDecisionResult.Denied("approval_not_found", "approval not found");
            case V2ApprovalOutcome.ConsumedDenied:
                return ApprovalDecisionResult.Denied("approval_denied", "approval denied");
            case V2ApprovalOutcome.ConsumedApproved:
                if (command == ApprovalCommandV2.AllowSessionDirectory)
                {
                    try
                    {
                        _session.DirectoryGrants.Add(
                            request.SessionId,
                            CanonicalDirectoryIdentity.CreateExisting(pending.Binding.BoundaryDirectory!));
                    }
                    catch
                    {
                        return ApprovalDecisionResult.Denied("grant_invalid", "approved directory is no longer available");
                    }
                }

                // Issue authorization token (60s) bound to snapshot + intent hash + tool identity
                var authToken = _authTokens.Issue(
                    requestId: pending.Binding.RunId,
                    sessionId: request.SessionId,
                    toolId: pending.Binding.ToolId,
                    toolVersion: pending.Binding.ToolVersion,
                    argumentsHash: pending.Binding.IntentHash,
                    lifetime: TimeSpan.FromSeconds(60),
                    snapshotVersion: pending.Binding.SnapshotVersion,
                    intentHash: pending.Binding.IntentHash);
                return ApprovalDecisionResult.Authorized(
                    authToken,
                    DateTimeOffset.UtcNow.AddSeconds(60).ToString("O"),
                    PendingApprovalStore.BuildMatcherPreview(pending.Binding));
            default:
                return ApprovalDecisionResult.Denied("approval_not_found", "approval not found");
        }
    }
}
