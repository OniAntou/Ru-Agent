using System.Collections.Concurrent;

namespace RuBackend.Harness.Reconciliation;

/// <summary>
/// Bridge between the V1 approvals endpoint and the per-run tool gateway: an approved
/// pending ask consumes the next ask for that run (allow-once semantics). Process-local
/// by design; a restart drops grants and the run re-enters reconciliation instead.
/// </summary>
public interface IAgentApprovalGrants
{
    void GrantNext(string runId);

    bool TryConsume(string runId);
}

public sealed class InMemoryAgentApprovalGrants : IAgentApprovalGrants
{
    private readonly ConcurrentDictionary<string, byte> _grants = new(StringComparer.Ordinal);

    public void GrantNext(string runId) =>
        _grants.TryAdd(runId ?? throw new ArgumentNullException(nameof(runId)), 0);

    public bool TryConsume(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return false;
        }

        return _grants.TryRemove(runId, out _);
    }
}
