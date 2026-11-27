using System.Collections.Concurrent;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Subagents;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Runtime;

public sealed partial class BrokerExecutionRuntime
{
    private readonly ConcurrentDictionary<string, DelegatedExecutionScope> _delegations = new(StringComparer.Ordinal);
    private const int MaximumRetainedDelegations = 1024;

    /// <summary>In-process Host seam. No model-visible envelope can bind authority.</summary>
    public async Task BindDelegationAsync(string parentRunId, string childRunId, SubagentRole role, CancellationToken ct,
        SubagentParentPolicyIdentity? expectedParentPolicy = null, BrokerPolicySnapshotWire? parentCeiling = null,
        bool detached = false)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            // A detached child admitted after its parent closed binds to the parent snapshot
            // retained while that lease was live; only a ceiling-bound bind may use it.
            var parent = LiveParent(parentRunId);
            var live = parent is not null;
            if (parent is null && detached && parentCeiling is not null
                && _detachedParents.TryGetValue(parentRunId, out var retained) && _session.Owns(retained.SessionId))
                parent = retained;
            if (!Enum.IsDefined(role) || parentRunId == childRunId || _delegations.ContainsKey(parentRunId)
                || parent is null
                || !_session.TryGetRunLease(childRunId, out var child) || child is null || child.IsExpired(DateTimeOffset.UtcNow)
                || child.Lifetime.IsRevoked
                || parent.SessionId == child.SessionId || parent.Preset != child.Preset
                || role == SubagentRole.Coding && parent.Preset == "plan")
                throw new AgentProtocolException("subagent_delegation_denied");
            if (!_delegations.ContainsKey(childRunId) && _delegations.Count >= MaximumRetainedDelegations)
                throw new AgentProtocolException("subagent_delegation_capacity");
            var ceiling = parentCeiling is null ? null : DelegationParentPolicy.ValidateCeiling(parent, parentCeiling);
            if (expectedParentPolicy is not null && ((ceiling?.Version ?? parent.SnapshotVersion) != expectedParentPolicy.Version
                || (ceiling?.Sha256 ?? parent.SnapshotHash) != expectedParentPolicy.Sha256))
                throw new AgentProtocolException("subagent_delegation_stale");
            var sameRoot = string.Equals(parent.CanonicalRoot, child.CanonicalRoot, StringComparison.OrdinalIgnoreCase);
            if (role == SubagentRole.Review && !sameRoot
                || role == SubagentRole.Coding && (sameRoot || DelegationPolicyEvaluator.Inside(child.CanonicalRoot, parent.CanonicalRoot)))
                throw new AgentProtocolException("subagent_workspace_invalid");
            var scope = new DelegatedExecutionScope(parentRunId, childRunId, parent.SessionId, child.SessionId,
                parent.CanonicalRoot, child.CanonicalRoot, ceiling?.Version ?? parent.SnapshotVersion,
                ceiling?.Sha256 ?? parent.SnapshotHash, role, ceiling)
                { ParentLifetime = parent.Lifetime, ChildLifetime = child.Lifetime, Detached = detached };
            // A detached child bound to a live parent (also after a Host restart, when nothing
            // was retained at spawn) must outlive that parent's close.
            if (detached && live && !_delegations.ContainsKey(childRunId)) RetainDetachedParentCore(parentRunId, parent);
            if (!_delegations.TryAdd(childRunId, scope)
                && (_delegations[childRunId] with { ParentCeiling = null }) != (scope with { ParentCeiling = null }))
                throw new AgentProtocolException("subagent_delegation_stale");
        }
        finally { _lifecycleGate.Release(); }
    }

    private PolicyEvaluation? EvaluateDelegation(BrokerRunLease child, PolicyContext context)
    {
        if (!_delegations.TryGetValue(child.RunId, out var scope)) return null;
        if (child.SessionId != scope.ChildSessionId
            || child.Lifetime != scope.ChildLifetime
            || !string.Equals(child.CanonicalRoot, scope.ChildRoot, StringComparison.OrdinalIgnoreCase))
            return new PolicyEvaluation("deny", "subagent_scope_denied", null);
        if (!TryGetDelegatingParent(scope, out var parent))
            return new PolicyEvaluation("deny", "subagent_parent_revoked", null);
        return DelegationPolicyEvaluator.Evaluate(scope, parent!, context, DateTimeOffset.UtcNow);
    }

    private BrokerRunLease? LiveParent(string parentRunId) =>
        _session.TryGetRunLease(parentRunId, out var parent) && parent is not null
            && !parent.IsExpired(DateTimeOffset.UtcNow) && !parent.Lifetime.IsRevoked ? parent : null;

    private bool TryGetDelegatingParent(DelegatedExecutionScope scope, out BrokerRunLease? parent)
    {
        parent = null;
        var valid = scope.ParentLifetime is { IsRevoked: false } && scope.ChildLifetime is { IsRevoked: false }
            && _session.TryGetRunLease(scope.ParentRunId, out parent) && parent is not null
            && parent.Lifetime == scope.ParentLifetime
            && !parent.IsExpired(DateTimeOffset.UtcNow) && parent.SessionId == scope.ParentSessionId
            && string.Equals(parent.CanonicalRoot, scope.ParentRoot, StringComparison.OrdinalIgnoreCase)
            && ParentPolicyCompatible(parent, scope);
        // A live parent always governs, including a policy change that revokes the child.
        // Only once that lease is closed does a detached child fall back to the retained snapshot.
        if (!valid && scope.Detached && LiveParent(scope.ParentRunId) is null)
            valid = TryGetRetainedParent(scope, out parent);
        if (valid && scope.ParentCeiling is { } ceiling)
            parent = parent! with { Snapshot = ceiling, SnapshotVersion = ceiling.Version, SnapshotHash = ceiling.Sha256 };
        return valid;
    }

    private static bool ParentPolicyCompatible(BrokerRunLease parent, DelegatedExecutionScope scope) =>
        scope.ParentCeiling is { } ceiling ? DelegationParentPolicy.Compatible(parent.Snapshot, ceiling)
            : parent.SnapshotVersion == scope.ParentSnapshotVersion && parent.SnapshotHash == scope.ParentSnapshotHash;

    private void RefreshDelegatedExecutions(BrokerRunLease parent)
    {
        foreach (var scope in _delegations.Values.Where(s => s.ParentRunId == parent.RunId))
            if (!ParentPolicyCompatible(parent, scope))
            {
                scope.ChildLifetime?.Revoke();
                _runCancellations.CancelRun(scope.ChildRunId);
            }
    }

    private void RevokeDelegatedExecutions(string runId)
    {
        if (_session.TryGetRunLease(runId, out var lease)) lease?.Lifetime.Revoke();
        if (_delegations.TryGetValue(runId, out var delegated)) delegated.ChildLifetime?.Revoke();
        // Closing a parent leaves its detached children alone; the Host cancels them by
        // their own run when the user cancels the family or deletes the session.
        foreach (var scope in _delegations.Values.Where(s => s.ParentRunId == runId && !s.Detached))
        {
            scope.ChildLifetime?.Revoke();
            _runCancellations.CancelRun(scope.ChildRunId);
        }
    }

    private void ReleaseSessionDelegations(string sessionId)
    {
        foreach (var scope in _delegations.Values.Where(s => s.ChildSessionId == sessionId))
        {
            scope.ChildLifetime?.Revoke();
            _delegations.TryRemove(scope.ChildRunId, out _);
        }
    }
}
