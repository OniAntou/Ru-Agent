using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;

namespace RuBackend.Harness;

/// <summary>Settles a failed run without constructing the provider or tool runtime that failed.</summary>
internal sealed class AgentRunFailureTransition(IHarnessEventStore store)
{
    private const int MaximumCommitAttempts = 8;
    private readonly IIdGenerator _ids = new CryptographicIdGenerator();

    public async Task<AgentRun?> FailAsync(RunId runId, string errorCode, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumCommitAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = await store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (run is null || run.State.IsTerminal()) return run;

            var transition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.Fail);
            if (!transition.Accepted)
                throw new HarnessStoreException(transition.ErrorCode ?? "invalid_transition");

            var failed = AgentV1Shared.AdvanceRun(run, transition.Next, run.BudgetWindow,
                run.ModelStepsInWindow, run.ToolCallsInWindow, 1, DateTimeOffset.UtcNow, errorCode);
            var result = await store.CommitAsync(new HarnessCommit(runId, run.Version, failed,
                [new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunFailed, safeCode: errorCode)], [], []),
                cancellationToken).ConfigureAwait(false);
            if (result.Outcome == HarnessCommitOutcome.Committed) return result.Run;
            if (result.Outcome == HarnessCommitOutcome.RunNotFound) return null;
        }

        // The caller must retain runtime ownership when no terminal transition was committed.
        throw new HarnessStoreException("commit_conflict");
    }
}
