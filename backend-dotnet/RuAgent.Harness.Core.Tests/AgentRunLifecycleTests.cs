using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Tests.Fakes;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

/// <summary>
/// Failure settlement is a concurrent transition: another writer (a client cancel, an
/// expired approval, startup recovery) can make the run terminal, or advance its version,
/// between the read a step holds and the failure commit. The runtime driver must treat the
/// durable terminal state as authority and rebase a lost version race instead of reporting
/// an internal driver failure, the same contract the Host's persistence-only failure
/// transition already implements.
/// </summary>
public sealed class AgentRunLifecycleTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Failing_an_already_failed_run_returns_it_without_a_second_commit()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var lifecycle = Lifecycle(store);

        var failed = await lifecycle.FailAsync(RunId(), "model_failed", CancellationToken.None);
        Assert.Equal(HarnessRunState.Failed, failed.State);
        Assert.Equal("model_failed", failed.TerminalErrorCode);
        Assert.Single(store.Commits);

        var settled = await lifecycle.FailAsync(RunId(), "runtime_drive_failed", CancellationToken.None);

        Assert.Equal(HarnessRunState.Failed, settled.State);
        Assert.Equal("model_failed", settled.TerminalErrorCode);
        Assert.Single(store.Commits);
    }

    [Fact]
    public async Task A_run_a_concurrent_writer_cancelled_keeps_its_own_terminal_code()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var lifecycle = Lifecycle(store);

        // The writer that won the race committed a terminal state first.
        var run = await store.GetRunAsync(RunId(), CancellationToken.None);
        var cancelled = Advance(
            run!,
            new HarnessRunStatus(HarnessRunState.Cancelled),
            "cancelled");
        var committed = await store.CommitAsync(
            new HarnessCommit(
                run!.RunId,
                run.Version,
                cancelled,
                [new HarnessEventIntent(new EventId("evt_" + Guid.NewGuid().ToString("N")), HarnessEventKind.RunStateChanged, safeCode: "cancelled")],
                [],
                []),
            CancellationToken.None);
        Assert.Equal(HarnessCommitOutcome.Committed, committed.Outcome);

        var settled = await lifecycle.FailAsync(RunId(), "runtime_drive_failed", CancellationToken.None);

        Assert.Equal(HarnessRunState.Cancelled, settled.State);
        Assert.Equal("cancelled", settled.TerminalErrorCode);
        var events = await ReadEventsAsync(store);
        Assert.DoesNotContain(events, item => item.Kind == "run.failed");
    }

    [Fact]
    public async Task A_version_conflict_is_rebased_instead_of_failing_the_transition()
    {
        var inner = new InMemoryHarnessEventStore();
        inner.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var store = new ConflictOnceStore(inner);
        var lifecycle = Lifecycle(store);

        var failed = await lifecycle.FailAsync(RunId(), "model_failed", CancellationToken.None);

        Assert.Equal(HarnessRunState.Failed, failed.State);
        Assert.Equal("model_failed", failed.TerminalErrorCode);
        Assert.Equal(2, store.CommitCalls);
        var failure = Assert.Single(inner.Commits.SelectMany(commit => commit.Events), item => item.Kind == HarnessEventKind.RunFailed);
        Assert.Equal("model_failed", failure.SafeCode);
    }

    [Fact]
    public async Task Repeated_version_conflicts_are_bounded_and_leave_the_run_running()
    {
        var inner = new InMemoryHarnessEventStore();
        inner.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var store = new AlwaysConflictingStore(inner);
        var lifecycle = Lifecycle(store);

        var error = await Assert.ThrowsAsync<AgentRunStateException>(() =>
            lifecycle.FailAsync(RunId(), "model_failed", CancellationToken.None));

        Assert.Equal("run_version_conflict", error.Code);
        Assert.Equal("run_version_conflict", error.Message);
        Assert.Equal(8, store.CommitCalls);
        Assert.Empty(inner.Commits);
        var run = await inner.GetRunAsync(RunId(), CancellationToken.None);
        Assert.Equal(HarnessRunState.Running, run!.State);
        Assert.Null(run.TerminalErrorCode);
    }

    private static AgentRunLifecycle Lifecycle(IHarnessEventStore store) =>
        new(store, new CryptographicIdGenerator(), new SystemTimeProvider(), cancellation: null);

    private static AgentRun Run(HarnessRunStatus status) =>
        new(
            RunId(),
            new SessionId("11111111111111111111111111111111"),
            new WorkspaceId("ws_11111111111111111111111111111111"),
            status,
            0,
            0,
            1,
            0,
            0,
            AgentPermissionPreset.Standard,
            1,
            new string('a', 64),
            Epoch,
            Epoch);

    private static AgentRun Advance(AgentRun run, HarnessRunStatus status, string? errorCode) =>
        run.Advance(status, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1, Epoch, errorCode);

    private static RunId RunId() =>
        new("run_11111111111111111111111111111111");

    private static async Task<List<HarnessEvent>> ReadEventsAsync(IHarnessEventStore store)
    {
        var events = new List<HarnessEvent>();
        await foreach (var envelope in store.ReadEventsAsync(RunId(), 0, CancellationToken.None))
        {
            events.Add(envelope.Event);
        }

        return events;
    }

    /// <summary>Returns one version conflict, then delegates: the retry must re-read and land.</summary>
    private sealed class ConflictOnceStore(IHarnessEventStore inner) : IHarnessEventStore
    {
        private int _conflicts;

        public int CommitCalls { get; private set; }

        public Task<CreateRunResult> TryCreateRunAsync(NewAgentRun request, CancellationToken cancellationToken) =>
            inner.TryCreateRunAsync(request, cancellationToken);

        public Task<AgentRun?> GetRunAsync(RunId runId, CancellationToken cancellationToken) =>
            inner.GetRunAsync(runId, cancellationToken);

        public IAsyncEnumerable<HarnessEventEnvelope> ReadEventsAsync(RunId runId, long afterSequence, CancellationToken cancellationToken) =>
            inner.ReadEventsAsync(runId, afterSequence, cancellationToken);

        public async Task<HarnessCommitResult> CommitAsync(HarnessCommit commit, CancellationToken cancellationToken)
        {
            CommitCalls++;
            if (Interlocked.Increment(ref _conflicts) == 1)
            {
                var current = await inner.GetRunAsync(commit.RunId, cancellationToken).ConfigureAwait(false);
                return HarnessCommitResult.VersionConflict(current!);
            }

            return await inner.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Never accepts the failure commit: the retry loop must stop at its bound.</summary>
    private sealed class AlwaysConflictingStore(IHarnessEventStore inner) : IHarnessEventStore
    {
        public int CommitCalls { get; private set; }

        public Task<CreateRunResult> TryCreateRunAsync(NewAgentRun request, CancellationToken cancellationToken) =>
            inner.TryCreateRunAsync(request, cancellationToken);

        public Task<AgentRun?> GetRunAsync(RunId runId, CancellationToken cancellationToken) =>
            inner.GetRunAsync(runId, cancellationToken);

        public IAsyncEnumerable<HarnessEventEnvelope> ReadEventsAsync(RunId runId, long afterSequence, CancellationToken cancellationToken) =>
            inner.ReadEventsAsync(runId, afterSequence, cancellationToken);

        public async Task<HarnessCommitResult> CommitAsync(HarnessCommit commit, CancellationToken cancellationToken)
        {
            CommitCalls++;
            var current = await inner.GetRunAsync(commit.RunId, cancellationToken).ConfigureAwait(false);
            return HarnessCommitResult.VersionConflict(current!);
        }
    }
}
