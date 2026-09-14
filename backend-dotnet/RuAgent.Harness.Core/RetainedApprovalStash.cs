using System.Collections.Concurrent;

namespace RuAgent.Harness.Core;

/// <summary>
/// Proposals parked while a run waits for approval. Only the proposals are kept:
/// prepared executions carry single-use authorization tokens that go stale across
/// the wait, so resume re-evaluates (fresh approval token) and prepares anew.
/// The model never has to re-propose.
/// </summary>
public sealed record RetainedApprovalStep(
    IReadOnlyList<ToolProposal> Proposals,
    ApprovalId ApprovalId,
    ModelStepId ModelStepId,
    DateTimeOffset StashedAt);

public interface IRetainedApprovalStash
{
    void Stash(RunId runId, RetainedApprovalStep step);

    bool TryPeek(RunId runId, out RetainedApprovalStep? step);

    bool TryTake(RunId runId, ApprovalId approvalId, out RetainedApprovalStep? step);

    void Drop(RunId runId);
}

/// <summary>
/// Process-local stash. Entries die with the backend (restart falls back to the
/// old re-propose flow) and are swept after 24h. One slot per run: a step returns
/// on the first Ask, so a second stash always supersedes the first.
/// </summary>
public sealed class InMemoryRetainedApprovalStash : IRetainedApprovalStash
{
    private static readonly TimeSpan TimeToLive = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, RetainedApprovalStep> _steps = new(StringComparer.Ordinal);

    public void Stash(RunId runId, RetainedApprovalStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        SweepExpired();
        _steps[runId.Value] = step;
    }

    public bool TryPeek(RunId runId, out RetainedApprovalStep? step)
    {
        if (_steps.TryGetValue(runId.Value, out var found) && !IsExpired(found))
        {
            step = found;
            return true;
        }

        step = null;
        return false;
    }

    public bool TryTake(RunId runId, ApprovalId approvalId, out RetainedApprovalStep? step)
    {
        if (_steps.TryGetValue(runId.Value, out var found)
            && !IsExpired(found)
            && string.Equals(found.ApprovalId.Value, approvalId.Value, StringComparison.Ordinal)
            && _steps.TryRemove(runId.Value, out step))
        {
            return true;
        }

        step = null;
        return false;
    }

    public void Drop(RunId runId)
    {
        _steps.TryRemove(runId.Value, out _);
    }

    private void SweepExpired()
    {
        foreach (var pair in _steps)
        {
            if (IsExpired(pair.Value))
            {
                _steps.TryRemove(pair.Key, out _);
            }
        }
    }

    private static bool IsExpired(RetainedApprovalStep step) =>
        DateTimeOffset.UtcNow - step.StashedAt > TimeToLive;
}
