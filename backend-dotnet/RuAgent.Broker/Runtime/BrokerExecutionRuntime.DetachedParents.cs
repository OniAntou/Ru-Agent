using System.Collections.Concurrent;
using RuAgent.Broker.Subagents;
using RuAgent.Protocol;

namespace RuAgent.Broker.Runtime;

/// <summary>
/// Parent snapshots retained for detached children (ADR 0045). A record is taken only from a
/// live, non-delegated parent lease, is never widened afterwards, and grants nothing on its
/// own: it lets a detached child keep evaluating against the parent policy it was spawned
/// under after that parent's lease closes normally.
/// </summary>
public sealed partial class BrokerExecutionRuntime
{
    private readonly ConcurrentDictionary<string, BrokerRunLease> _detachedParents = new(StringComparer.Ordinal);

    /// <summary>In-process Host seam, called while the spawning parent is live.</summary>
    public async Task RetainDetachedParentAsync(string parentRunId, CancellationToken ct)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var parent = LiveParent(parentRunId);
            if (parent is null || _delegations.ContainsKey(parentRunId))
                throw new AgentProtocolException("subagent_delegation_denied");
            RetainDetachedParentCore(parentRunId, parent);
        }
        finally { _lifecycleGate.Release(); }
    }

    // Caller holds the lifecycle gate and passes a live, non-delegated parent lease.
    private void RetainDetachedParentCore(string parentRunId, BrokerRunLease parent)
    {
        if (!_detachedParents.ContainsKey(parentRunId) && _detachedParents.Count >= MaximumRetainedDelegations
            && !TryEvictIdleDetachedParent())
            throw new AgentProtocolException("subagent_delegation_capacity");
        _detachedParents[parentRunId] = parent;
    }

    private bool TryGetRetainedParent(DelegatedExecutionScope scope, out BrokerRunLease? parent)
    {
        parent = null;
        if (scope.ChildLifetime is not { IsRevoked: false }
            || !_detachedParents.TryGetValue(scope.ParentRunId, out var retained)
            || retained.Lifetime != scope.ParentLifetime || retained.SessionId != scope.ParentSessionId
            || !_session.Owns(retained.SessionId)
            || !string.Equals(retained.CanonicalRoot, scope.ParentRoot, StringComparison.OrdinalIgnoreCase)
            || !ParentPolicyCompatible(retained, scope))
            return false;
        parent = retained;
        return true;
    }

    // Capacity never revokes a running child: only a parent no live detached child
    // still evaluates against may be dropped. A queued child of that parent then fails
    // closed when it binds.
    private bool TryEvictIdleDetachedParent()
    {
        foreach (var runId in _detachedParents.Keys)
        {
            if (LiveParent(runId) is not null) continue;
            if (_delegations.Values.Any(s => s.Detached && s.ParentRunId == runId && s.ChildLifetime is { IsRevoked: false }))
                continue;
            if (_detachedParents.TryRemove(runId, out _)) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="changes"/> come from a detached child of an earlier root in the
    /// applying root's session. Only a retained record proves that; without one (another
    /// session, a Host restart, an evicted record) the apply is denied as before.
    /// </summary>
    private bool IsDetachedOwner(BrokerRunLease root, RuAgent.Protocol.Subagents.SubagentChangeSet changes) =>
        changes.ParentRunId != root.RunId
        && _detachedParents.TryGetValue(changes.ParentRunId, out var owner)
        && owner.SessionId == root.SessionId
        && string.Equals(owner.CanonicalRoot, root.CanonicalRoot, StringComparison.OrdinalIgnoreCase);

    /// <summary>Closing the parent's session ends every detached child it spawned.</summary>
    private void ReleaseDetachedParents(string sessionId)
    {
        foreach (var scope in _delegations.Values.Where(s => s.Detached && s.ParentSessionId == sessionId))
        {
            scope.ChildLifetime?.Revoke();
            _runCancellations.CancelRun(scope.ChildRunId);
        }
        foreach (var pair in _detachedParents.Where(p => p.Value.SessionId == sessionId))
            _detachedParents.TryRemove(pair.Key, out _);
    }
}
