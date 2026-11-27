using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

/// <summary>
/// Losing an optimistic commit race before a step dispatches is not a run failure: the
/// step was recorded against a version another writer advanced (a compaction or skill
/// command, recovery), so the pass re-reads the run and the next pass decides again. A
/// conflict after a tool executed must stay a failure instead: there is no receipt to
/// retry the dispatch from.
/// </summary>
public sealed partial class AgentRunCoordinatorTests
{
    [Fact]
    public async Task A_version_conflict_before_the_model_step_redrives_the_run()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("done"));
        var inner = new InMemoryHarnessEventStore();
        inner.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var store = new ConflictOnCommitStore(inner, HarnessEventKind.ModelStepStarted);
        var coordinator = Coordinator(model, new FakeToolGateway(), store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal("run_changed_during_generation", result.ErrorCode);
        Assert.Equal(HarnessRunState.Running, result.Run.State);
        Assert.Empty(model.Requests);
        Assert.Empty(inner.Commits);
        var current = await inner.GetRunAsync(RunId(), CancellationToken.None);
        Assert.Equal(HarnessRunState.Running, current!.State);
        Assert.Null(current.TerminalErrorCode);
    }

    [Fact]
    public async Task A_version_conflict_before_a_tool_dispatches_redrives_without_executing_it()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway();
        var inner = new InMemoryHarnessEventStore();
        inner.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var store = new ConflictOnCommitStore(inner, HarnessEventKind.ModelStepCompleted);
        var coordinator = Coordinator(model, gateway, store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal("run_changed_during_generation", result.ErrorCode);
        Assert.Equal(HarnessRunState.Running, result.Run.State);
        Assert.Empty(gateway.ExecuteAttempts);
        Assert.DoesNotContain(
            inner.Commits.SelectMany(commit => commit.Events),
            item => item.Kind == HarnessEventKind.RunFailed);
        var current = await inner.GetRunAsync(RunId(), CancellationToken.None);
        Assert.Equal(HarnessRunState.Running, current!.State);
        Assert.Null(current.TerminalErrorCode);
    }

    /// <summary>Returns one version conflict for the first commit carrying the event kind, then delegates.</summary>
    private sealed class ConflictOnCommitStore(IHarnessEventStore inner, HarnessEventKind kind) : IHarnessEventStore
    {
        private int _conflicted;

        public Task<CreateRunResult> TryCreateRunAsync(NewAgentRun request, CancellationToken cancellationToken) =>
            inner.TryCreateRunAsync(request, cancellationToken);

        public Task<AgentRun?> GetRunAsync(RunId runId, CancellationToken cancellationToken) =>
            inner.GetRunAsync(runId, cancellationToken);

        public IAsyncEnumerable<HarnessEventEnvelope> ReadEventsAsync(RunId runId, long afterSequence, CancellationToken cancellationToken) =>
            inner.ReadEventsAsync(runId, afterSequence, cancellationToken);

        public async Task<HarnessCommitResult> CommitAsync(HarnessCommit commit, CancellationToken cancellationToken)
        {
            if (commit.Events.Any(item => item.Kind == kind)
                && Interlocked.CompareExchange(ref _conflicted, 1, 0) == 0)
            {
                var current = await inner.GetRunAsync(commit.RunId, cancellationToken).ConfigureAwait(false);
                return HarnessCommitResult.VersionConflict(current!);
            }

            return await inner.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        }
    }
}
