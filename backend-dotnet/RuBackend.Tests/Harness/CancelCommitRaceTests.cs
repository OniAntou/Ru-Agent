using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Protocol.Harness;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class CancelCommitRaceTests
{
    [Theory]
    [InlineData(HarnessRunState.Running)]
    [InlineData(HarnessRunState.Cancelled)]
    [InlineData(HarnessRunState.Completed)]
    public async Task Cancel_accepts_worker_progress_during_tool_shutdown(HarnessRunState workerState)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var seeded = await fixture.SeedRunAsync(new HarnessRunStatus(HarnessRunState.Running));
        var roots = new ShutdownProgressRoots(async (runId, ct) =>
        {
            var run = (await fixture.EventStore.GetRunAsync(runId, ct))!;
            var next = AgentV1Shared.AdvanceRun(run, new HarnessRunStatus(workerState),
                run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1, DateTimeOffset.UtcNow);
            var result = await fixture.EventStore.CommitAsync(new HarnessCommit(runId, run.Version, next,
                [new HarnessEventIntent(new CryptographicIdGenerator().NewEventId(),
                    HarnessEventKind.RunStateChanged, safeCode: "worker_settled")], [], []), ct);
            Assert.Equal(HarnessCommitOutcome.Committed, result.Outcome);
        });
        using var services = new AgentV1ServiceTestBuilder(fixture).WithRuntimeRoots(roots).Build();

        var cancelled = await services.Lifecycle.CancelAsync(seeded.Run.RunId.Value, default);
        var replay = await services.Lifecycle.CancelAsync(seeded.Run.RunId.Value, default);

        Assert.Equal(200, cancelled.StatusCode);
        Assert.Equal(200, replay.StatusCode);
        var final = await fixture.EventStore.GetRunAsync(seeded.Run.RunId, default);
        Assert.Equal(workerState == HarnessRunState.Completed ? HarnessRunState.Completed : HarnessRunState.Cancelled,
            final!.State);
        Assert.Equal(1, roots.ShutdownCount);
        Assert.Equal(workerState == HarnessRunState.Running ? 2 : 1,
            final.Version - seeded.Run.Version);
    }

    [Theory]
    [InlineData(1, 200)]
    [InlineData(8, 409)]
    public async Task Cancel_retries_version_conflicts_with_a_bound(int conflicts, int expectedStatus)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var seeded = await fixture.SeedRunAsync(new HarnessRunStatus(HarnessRunState.Running));
        var store = new ConflictingStore(fixture.EventStore, conflicts);
        var roots = new ShutdownProgressRoots((_, _) => Task.CompletedTask);
        using var services = new AgentV1ServiceTestBuilder(fixture)
            .WithRuntimeRoots(roots).WithLifecycleStore(store).Build();

        var result = await services.Lifecycle.CancelAsync(seeded.Run.RunId.Value, default);

        Assert.Equal(expectedStatus, result.StatusCode);
        Assert.Equal(Math.Min(conflicts + 1, 8), store.CommitCalls);
        Assert.Equal(1, roots.ShutdownCount);
        var final = await fixture.EventStore.GetRunAsync(seeded.Run.RunId, default);
        Assert.Equal(expectedStatus == 200 ? HarnessRunState.Cancelled : HarnessRunState.Running, final!.State);
    }

    private sealed class ConflictingStore(IHarnessEventStore inner, int conflicts) : IHarnessEventStore
    {
        public int CommitCalls { get; private set; }
        public Task<CreateRunResult> TryCreateRunAsync(NewAgentRun request, CancellationToken ct) => inner.TryCreateRunAsync(request, ct);
        public Task<AgentRun?> GetRunAsync(RunId id, CancellationToken ct) => inner.GetRunAsync(id, ct);
        public IAsyncEnumerable<HarnessEventEnvelope> ReadEventsAsync(RunId id, long after, CancellationToken ct) => inner.ReadEventsAsync(id, after, ct);
        public async Task<HarnessCommitResult> CommitAsync(HarnessCommit commit, CancellationToken ct)
        {
            CommitCalls++;
            if (CommitCalls <= conflicts)
            {
                var run = (await inner.GetRunAsync(commit.RunId, ct))!;
                var next = AgentV1Shared.AdvanceRun(run, run.Status,
                    run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1, DateTimeOffset.UtcNow);
                var progress = await inner.CommitAsync(new HarnessCommit(run.RunId, run.Version, next,
                    [new HarnessEventIntent(new CryptographicIdGenerator().NewEventId(),
                        HarnessEventKind.RunStateChanged, safeCode: "worker_progress")], [], []), ct);
                Assert.Equal(HarnessCommitOutcome.Committed, progress.Outcome);
            }
            return await inner.CommitAsync(commit, ct);
        }
    }

    private sealed class ShutdownProgressRoots(Func<RunId, CancellationToken, Task> shutdown)
        : IAgentRuntimeRootRegistry
    {
        private readonly AgentRuntimeRootRegistry _inner = new();
        public int ShutdownCount { get; private set; }
        public void Register(RunId id, string root) => _inner.Register(id, root);
        public void Register(RunId id, string root, PolicySnapshot policy) => _inner.Register(id, root, policy);
        public bool TryGetRoot(RunId id, out string? root) => _inner.TryGetRoot(id, out root);
        public bool TryGetPolicySnapshot(RunId id, out PolicySnapshot? policy) => _inner.TryGetPolicySnapshot(id, out policy);
        public void RegisterCoordinator(RunId id, AgentRunCoordinator coordinator) => _inner.RegisterCoordinator(id, coordinator);
        public bool TryGetCoordinator(RunId id, out AgentRunCoordinator? coordinator) => _inner.TryGetCoordinator(id, out coordinator);
        public void Remove(RunId id) => _inner.Remove(id);
        public IReadOnlyList<(string RunId, string WorkspaceRoot)> Snapshot() => _inner.Snapshot();
        public Task CancelRunningToolsAsync(RunId id, CancellationToken ct)
        {
            ShutdownCount++;
            return shutdown(id, ct);
        }
    }
}
