using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class ModelStepFailureClassificationTests
{
    private static readonly DateTimeOffset Epoch =
        new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Stable_carrier_code_reaches_the_run_unchanged()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var coordinator = CreateCoordinator(new ThrowingRunner(new StableError("catalog_unauthorized")), store);

        var error = await Assert.ThrowsAsync<StableError>(() =>
            coordinator.RunOneStepAsync(RunId(), CancellationToken.None));

        Assert.Equal("catalog_unauthorized", error.StepErrorCode);
        AssertFailedWith(store, "catalog_unauthorized");
    }

    [Fact]
    public async Task Unstable_carrier_code_falls_back_to_model_failed()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var coordinator = CreateCoordinator(new ThrowingRunner(new StableError("Bogus Code!")), store);

        await Assert.ThrowsAsync<StableError>(() =>
            coordinator.RunOneStepAsync(RunId(), CancellationToken.None));

        AssertFailedWith(store, "model_failed");
    }

    [Fact]
    public async Task Plain_exception_stays_model_failed()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var coordinator = CreateCoordinator(new ThrowingRunner(new InvalidOperationException("boom")), store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RunOneStepAsync(RunId(), CancellationToken.None));

        AssertFailedWith(store, "model_failed");
    }

    private static void AssertFailedWith(InMemoryHarnessEventStore store, string code)
    {
        var run = store.GetRunAsync(RunId(), CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(HarnessRunState.Failed, run!.State);
        Assert.Contains(
            store.Commits.SelectMany(commit => commit.Events),
            item => item.Kind == HarnessEventKind.RunFailed && item.SafeCode == code);
    }

    private static AgentRunCoordinator CreateCoordinator(
        IModelStepRunner model,
        InMemoryHarnessEventStore store) =>
        new(
            model,
            new FakeToolGateway(),
            store,
            new AgentContextAssembler(new EmptyContextSource()),
            new CryptographicIdGenerator(),
            new SystemTimeProvider(),
            new ToolCallScheduler(),
            new TransientFrameBuffer(32),
            promptContextProvider: new FakeSystemPromptContextProvider());

    private static AgentRun Run() =>
        new(
            RunId(),
            new SessionId(new string('1', 32)),
            new WorkspaceId("ws_" + new string('2', 32)),
            new HarnessRunStatus(HarnessRunState.Running),
            version: 0,
            lastSequence: 0,
            budgetWindow: 1,
            modelStepsInWindow: 0,
            toolCallsInWindow: 0,
            AgentPermissionPreset.Standard,
            policySnapshotVersion: 1,
            policySnapshotSha256: new string('a', 64),
            createdAt: Epoch,
            updatedAt: Epoch);

    private static RunId RunId() => new("run_" + new string('3', 32));

    private sealed class ThrowingRunner(Exception failure) : IModelStepRunner
    {
        public Task<ModelStepResult> RunStepAsync(
            ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta,
            CancellationToken cancellationToken) =>
            Task.FromException<ModelStepResult>(failure);
    }

    private sealed class StableError(string code) : Exception(code), IStableStepError
    {
        public string StepErrorCode { get; } = code;
    }

    private sealed class EmptyContextSource : IModelContextSource
    {
        public Task<ModelContextSourceSnapshot> LoadAsync(
            RunId runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ModelContextSourceSnapshot(
                Array.Empty<ModelContextEntry>(),
                Array.Empty<ModelContextEntry>()));
    }
}
