using System.Text.Json;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Subagents.Integration;
using RuAgent.Broker.Tools.Files;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Runtime;

public sealed partial class BrokerExecutionRuntime
{
    private const string SubagentApplyTool = "agent_apply";

    /// <summary>
    /// Trusted Host seam for a persisted coding result. Bytes and ownership must come from
    /// the frozen store, never from model arguments. Does not advertise a tool or mutate files.
    /// </summary>
    public Task<ToolEvaluateResult> EvaluateSubagentApplyAsync(string sessionId, string runId,
        SubagentChangeSet changes, CancellationToken ct) => UnderLifecycleAsync(() =>
    {
        var root = SubagentApplyRoot(sessionId, runId);
        using var handle = DirectoryHandleLease.Open(root.CanonicalRoot, requireCanonicalPath: true);
        var preparation = SubagentApplyPreparation.Create(root, changes);
        var decision = EvaluateSubagentFiles(root, preparation);
        if (decision.Decision == "deny")
            return new ToolEvaluateResult(null, DecisionReply("deny", decision.ReasonCode, decision.MatchedRuleId));

        var intent = SubagentApplyIntent(root, preparation, handle.Identity);
        if (decision.Decision == "allow")
        {
            var token = _authTokens.Issue(runId, sessionId, SubagentApplyTool, "1.0.0", intent,
                TimeSpan.FromSeconds(60), root.SnapshotVersion, intent);
            return new ToolEvaluateResult(null, DecisionReply("allow", "subagent_apply_allowed", null) with
                { AuthorizationToken = token });
        }

        var expires = DateTimeOffset.UtcNow.AddMinutes(30);
        var binding = new V2ApprovalBinding(runId, "apply_" + intent, SubagentApplyTool, "1.0.0",
            root.CanonicalRoot, null, [], null, PolicyOperationClasses.Mutation, PolicyNetworkIntents.None,
            root.SnapshotVersion, intent, ApprovalKind: "subagent_apply");
        var id = _pendingV2.IssueV2(binding, expires);
        var preview = PendingApprovalStore.BuildMatcherPreview(binding);
        preview["agent_id"] = changes.AgentId;
        preview["result_version"] = changes.ResultVersion;
        preview["file_count"] = preparation.Changes.Files.Count;
        preview["result_fingerprint"] = preparation.Fingerprint;
        preview["safe_summary"] = "apply frozen coding result; approval is single-use";
        return new ToolEvaluateResult(null, DecisionReply("ask", "subagent_apply_approval_required", decision.MatchedRuleId) with
        {
            ApprovalId = id, ApprovalKind = "subagent_apply", ExpiresAt = expires.ToString("O"), MatcherPreview = preview,
        });
    }, ct);

    /// <summary>Consumes exact authorization once; all writes stay inside Broker and the run cancellation boundary.</summary>
    public async Task<SubagentApplyOutcome> ApplySubagentChangesAsync(string sessionId, string runId,
        SubagentChangeSet changes, string authorizationToken, ISubagentApplyJournal journal, CancellationToken ct)
    {
        using var operation = _runCancellations.Register(runId, ct);
        DirectoryHandleLease? rootHandle = null;
        try
        {
            BrokerRunLease root;
            SubagentApplyPreparation preparation;
            try
            {
                await _lifecycleGate.WaitAsync(operation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && operation.Token.IsCancellationRequested)
            {
                throw new AgentProtocolException("subagent_apply_denied");
            }

            try
            {
                root = SubagentApplyRoot(sessionId, runId);
                rootHandle = DirectoryHandleLease.Open(root.CanonicalRoot, requireCanonicalPath: true);
                preparation = SubagentApplyPreparation.Create(root, changes);
                if (EvaluateSubagentFiles(root, preparation).Decision == "deny")
                    throw new AgentProtocolException("subagent_apply_denied");
                var intent = SubagentApplyIntent(root, preparation, rootHandle.Identity);
                var binding = new ApprovalBinding(runId, sessionId, SubagentApplyTool, "1.0.0", intent,
                    DateTimeOffset.UtcNow.AddSeconds(60), root.SnapshotVersion, intent);
                if (!_authTokens.TryConsume(authorizationToken, binding, out _))
                    throw new AgentProtocolException("decision_stale");
            }
            finally { _lifecycleGate.Release(); }

            return await new SubagentApplyAdapter(journal).ApplyAsync(root, preparation, true, GuardWrite, operation.Token)
                .ConfigureAwait(false);

            async Task GuardWrite(Action write, CancellationToken cancellation)
            {
                await _lifecycleGate.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    var current = SubagentApplyRoot(sessionId, runId);
                    if (current.Lifetime != root.Lifetime || current.BindingVersion != root.BindingVersion || current.SnapshotVersion != root.SnapshotVersion
                        || current.SnapshotHash != root.SnapshotHash || current.CanonicalRoot != root.CanonicalRoot)
                        throw new AgentProtocolException("decision_stale");
                    // Re-resolution and the held-file mutation happen under the same lifecycle
                    // gate as policy rebind/session teardown. Run cancellation also signals
                    // the operation token independently and is observed between file writes.
                    write();
                }
                finally { _lifecycleGate.Release(); }
            }
        }
        finally { rootHandle?.Dispose(); }
    }

    private BrokerRunLease SubagentApplyRoot(string sessionId, string runId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_session.Owns(sessionId) || !_session.TryGetRunLease(runId, out var root) || root is null
            || root.SessionId != sessionId || root.IsExpired(DateTimeOffset.UtcNow) || root.Preset == "plan"
            || _delegations.ContainsKey(runId) || root.Lifetime.IsRevoked)
            throw new AgentProtocolException("subagent_apply_denied");
        return root;
    }

    private static PolicyEvaluation EvaluateSubagentFiles(BrokerRunLease root, SubagentApplyPreparation preparation)
    {
        var outcome = new PolicyEvaluation("allow", "subagent_apply_allowed", null);
        foreach (var file in preparation.Changes.Files)
        {
            var context = SubagentApplyPreparation.Resolve(root, file);
            var next = SubagentApplyPreparation.Evaluate(root, context);
            if (next.Decision == "deny") return next;
            if (next.Decision == "ask") outcome = next;
        }
        return outcome;
    }

    private static string SubagentApplyIntent(BrokerRunLease root, SubagentApplyPreparation preparation, string identity) =>
        FileHashService.ComputeBytesHash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            preparation.Fingerprint, root.RunId, root.SessionId, root.BindingVersion, root.SnapshotHash,
            LeaseGeneration = root.Lifetime.Id,
            RootIdentity = identity,
            Targets = preparation.Changes.Files.Select(f => SubagentApplyPreparation.Resolve(root, f).IntentHash),
        }));
}
