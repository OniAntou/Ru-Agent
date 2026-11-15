using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Core.Tests;

public sealed class ApprovalWorkflowTests
{
    private static readonly DateTimeOffset Epoch =
        new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Approval_wait_and_resume_do_not_consume_model_step_or_tool_call_budget()
    {
        var store = new InMemoryHarnessEventStore();
        var run = Run(HarnessRunState.Running, modelStepsInWindow: 2, toolCallsInWindow: 3);
        store.Seed(run);
        var coordinator = Coordinator(store);

        var waiting = await coordinator.EnterApprovalWaitAsync(
            run.RunId,
            new ApprovalId("apr_" + new string('4', 32)),
            CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingApproval, waiting.State);
        Assert.Equal(2, waiting.ModelStepsInWindow);
        Assert.Equal(3, waiting.ToolCallsInWindow);

        var resumed = await coordinator.ResumeAfterApprovalAsync(run.RunId, CancellationToken.None);

        Assert.Equal(HarnessRunState.Running, resumed.State);
        Assert.Equal(2, resumed.ModelStepsInWindow);
        Assert.Equal(3, resumed.ToolCallsInWindow);
        Assert.Contains(
            store.Commits,
            commit => commit.Events.Any(@event => @event.Kind == HarnessEventKind.ApprovalRequested));
    }

    [Fact]
    public async Task Stale_or_terminal_resolution_has_no_effect_on_other_runs()
    {
        var store = new InMemoryHarnessEventStore();
        var run = Run(HarnessRunState.Running);
        store.Seed(run);
        var coordinator = Coordinator(store);
        await coordinator.EnterApprovalWaitAsync(
            run.RunId,
            new ApprovalId("apr_" + new string('5', 32)),
            CancellationToken.None);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            coordinator.ResumeAfterApprovalAsync(
                new RunId("run_" + new string('6', 32)),
                CancellationToken.None));

        var stillResumable = await coordinator.ResumeAfterApprovalAsync(run.RunId, CancellationToken.None);
        Assert.Equal(HarnessRunState.Running, stillResumable.State);
    }

    [Fact]
    public async Task Approval_wait_is_illegal_outside_running_state()
    {
        var store = new InMemoryHarnessEventStore();
        var completedRun = Run(HarnessRunState.Completed);
        store.Seed(completedRun);
        var coordinator = Coordinator(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.EnterApprovalWaitAsync(
                completedRun.RunId,
                new ApprovalId("apr_" + new string('7', 32)),
                CancellationToken.None));
    }

    private static AgentRunCoordinator Coordinator(InMemoryHarnessEventStore store) =>
        new(
            new FakeModelStepRunner(),
            new FakeToolGateway(),
            store,
            new AgentContextAssembler(new EmptyContextSource()),
            new SequenceIdGenerator(),
            new FixedTimeProvider(Epoch),
            new ToolCallScheduler(),
            new TransientFrameBuffer(32),
            promptContextProvider: new FakeSystemPromptContextProvider());

    private static AgentRun Run(
        HarnessRunState state,
        int modelStepsInWindow = 0,
        int toolCallsInWindow = 0)
    {
        var status = new HarnessRunStatus(state);
        var isTerminal = state is HarnessRunState.Completed
            or HarnessRunState.Failed
            or HarnessRunState.Cancelled
            or HarnessRunState.Interrupted;
        return new(
            new RunId("run_" + new string('0', 32)),
            new SessionId("11111111111111111111111111111111"),
            new WorkspaceId("ws_11111111111111111111111111111111"),
            status,
            version: 0,
            lastSequence: 0,
            budgetWindow: 1,
            modelStepsInWindow,
            toolCallsInWindow,
            AgentPermissionPreset.Standard,
            policySnapshotVersion: 1,
            new string('a', 64),
            Epoch,
            Epoch,
            terminalAt: isTerminal ? Epoch : null,
            terminalErrorCode: state is HarnessRunState.Failed or HarnessRunState.Interrupted ? "test_terminal" : null);
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

    private sealed class FixedTimeProvider : ITimeProvider
    {
        public FixedTimeProvider(DateTimeOffset now) => UtcNow = now;

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class SequenceIdGenerator : IIdGenerator
    {
        private ulong _counter;

        public RunId NewRunId() => new($"run_{++_counter:x32}");
        public ModelStepId NewModelStepId() => new($"step_{++_counter:x32}");
        public ToolCallId NewToolCallId() => new($"call_{++_counter:x32}");
        public ExecutionId NewExecutionId() => new($"exec_{++_counter:x32}");
        public ApprovalId NewApprovalId() => new($"apr_{++_counter:x32}");
        public InputId NewInputId() => new($"inp_{++_counter:x32}");
        public TrustRequestId NewTrustRequestId() => new($"trust_{++_counter:x32}");
        public PolicyRuleId NewPolicyRuleId() => new($"rule_{++_counter:x32}");
        public EventId NewEventId() => new($"evt_{++_counter:x32}");
        public PayloadId NewPayloadId() => new($"payload_{++_counter:x32}");
    }
}
