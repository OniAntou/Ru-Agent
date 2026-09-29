using RuAgent.Harness.Core.Approval;

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

public sealed class InMemoryRetainedApprovalStash : IRetainedApprovalStash
{
    private static readonly TimeSpan TimeToLive = ApprovalState.DefaultLifetime;
    private readonly object _gate = new();
    private readonly Dictionary<string, RetainedApprovalStep> _steps = new(StringComparer.Ordinal);
    private readonly ITimeProvider? _time;

    public InMemoryRetainedApprovalStash(ITimeProvider? time = null) => _time = time;

    public void Stash(RunId runId, RetainedApprovalStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        lock (_gate)
        {
            SweepExpired();
            _steps[runId.Value] = step;
        }
    }

    public bool TryPeek(RunId runId, out RetainedApprovalStep? step)
    {
        lock (_gate)
        {
            // Sweep here too: without it, entries for runs other than this one
            // stay in the dictionary until the next Stash, even though every
            // read already treats them as gone.
            SweepExpired();
            if (_steps.TryGetValue(runId.Value, out var found)
                && !IsExpired(found))
            {
                step = found;
                return true;
            }

            _steps.Remove(runId.Value);
            step = null;
            return false;
        }
    }

    public bool TryTake(RunId runId, ApprovalId approvalId, out RetainedApprovalStep? step)
    {
        lock (_gate)
        {
            if (!_steps.TryGetValue(runId.Value, out var found))
            {
                step = null;
                return false;
            }

            if (IsExpired(found))
            {
                _steps.Remove(runId.Value);
                step = null;
                return false;
            }

            if (!string.Equals(found.ApprovalId.Value, approvalId.Value, StringComparison.Ordinal))
            {
                step = null;
                return false;
            }

            _steps.Remove(runId.Value);
            step = found;
            return true;
        }
    }

    public void Drop(RunId runId)
    {
        lock (_gate)
        {
            _steps.Remove(runId.Value);
            SweepExpired();
        }
    }

    private void SweepExpired()
    {
        foreach (var pair in _steps.ToArray())
        {
            if (IsExpired(pair.Value))
            {
                _steps.Remove(pair.Key);
            }
        }
    }

    private bool IsExpired(RetainedApprovalStep step) =>
        Now() - step.StashedAt > TimeToLive;

    private DateTimeOffset Now() => _time?.UtcNow ?? DateTimeOffset.UtcNow;
}
