using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Core;

public interface IHarnessEventStore
{
    Task<CreateRunResult> TryCreateRunAsync(
        NewAgentRun request,
        CancellationToken cancellationToken);

    Task<AgentRun?> GetRunAsync(RunId runId, CancellationToken cancellationToken);

    Task<HarnessCommitResult> CommitAsync(
        HarnessCommit commit,
        CancellationToken cancellationToken);

    IAsyncEnumerable<HarnessEventEnvelope> ReadEventsAsync(
        RunId runId,
        long afterSequence,
        CancellationToken cancellationToken);
}
