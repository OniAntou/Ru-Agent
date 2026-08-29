using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class ReconciliationStateTests
{
    private static readonly DateTimeOffset Epoch =
        new(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------
    // Inspection gate.
    // ------------------------------------------------------------------

    [Fact]
    public void Activated_gate_denies_mutations_until_one_inspection()
    {
        var gate = new ReconciliationInspectionGate();
        var runId = RunId();

        Assert.False(gate.IsMutationDenied(runId));

        gate.Activate(runId);
        Assert.True(gate.IsMutationDenied(runId));

        gate.RecordInspection(runId);
        Assert.False(gate.IsMutationDenied(runId));
    }

    [Fact]
    public void Gate_is_scoped_per_run()
    {
        var gate = new ReconciliationInspectionGate();
        gate.Activate(new RunId("run_" + new string('a', 32)));

        Assert.True(gate.IsMutationDenied(new RunId("run_" + new string('a', 32))));
        Assert.False(gate.IsMutationDenied(new RunId("run_" + new string('b', 32))));
    }

    // ------------------------------------------------------------------
    // Typed user command parsing.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("applied", ReconciliationCommandKind.Applied)]
    [InlineData("not_applied", ReconciliationCommandKind.NotApplied)]
    [InlineData("unknown_continue", ReconciliationCommandKind.UnknownContinue)]
    public void Reconcile_commands_parse_from_wire_tokens(
        string token,
        ReconciliationCommandKind expected)
    {
        Assert.True(ReconciliationCommand.TryParse(token, out var command));
        Assert.Equal(expected, command.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("applied please")]
    [InlineData("APPLIED")]
    [InlineData(null)]
    public void Malformed_reconcile_commands_fail_closed(string? token)
    {
        Assert.False(ReconciliationCommand.TryParse(token, out _));
    }

    // ------------------------------------------------------------------
    // Cancellation race: cancelled_side_effects_possible requires inspection.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Cancelled_side_effecting_tool_moves_the_run_to_reconciliation_required()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway(ToolAccessKind.Mutation)
        {
            ForcedReceipt = new ToolExecutionReceipt(
                ExecutionId(),
                ToolCallId(),
                ToolExecutionOutcome.Cancelled,
                "process_cancelled",
                null,
                sideEffectsPossible: true),
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var coordinator = Coordinator(model, gateway, store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        var receipt = Assert.Single(result.Receipts);
        Assert.Equal(ToolExecutionOutcome.Cancelled, receipt.Outcome);
        Assert.True(receipt.SideEffectsPossible);
        Assert.Equal(HarnessRunState.ReconciliationRequired, result.Run.State);
        Assert.Contains(
            store.Commits.SelectMany(commit => commit.Events),
            item => item.Kind == HarnessEventKind.ReconciliationRequired);
    }

    [Fact]
    public async Task Timed_out_side_effecting_tool_also_requires_reconciliation()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway(ToolAccessKind.Mutation)
        {
            ForcedReceipt = new ToolExecutionReceipt(
                ExecutionId(),
                ToolCallId(),
                ToolExecutionOutcome.TimedOut,
                "process_timed_out",
                null,
                sideEffectsPossible: true),
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var coordinator = Coordinator(model, gateway, store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Equal(HarnessRunState.ReconciliationRequired, result.Run.State);
    }

    [Fact]
    public async Task Cancelled_read_only_tool_does_not_require_reconciliation()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway(ToolAccessKind.ReadOnly)
        {
            ForcedReceipt = new ToolExecutionReceipt(
                ExecutionId(),
                ToolCallId(),
                ToolExecutionOutcome.Cancelled,
                "process_cancelled",
                null,
                sideEffectsPossible: false),
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var coordinator = Coordinator(model, gateway, store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Equal(HarnessRunState.Running, result.Run.State);
    }

    // ------------------------------------------------------------------
    // Explicit resolution and the inspection requirement for UnknownContinue.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Applied_resolution_returns_the_run_to_running_without_a_gate()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway(ToolAccessKind.Mutation)
        {
            ForcedReceipt = CancelledReceipt(),
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var coordinator = Coordinator(model, gateway, store);
        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        var resolved = await coordinator.ApplyReconciliationAsync(
            RunId(),
            ReconciliationCommand.Applied,
            CancellationToken.None);

        Assert.Equal(HarnessRunState.Running, resolved.State);
        Assert.Contains(
            store.Commits.SelectMany(commit => commit.Events),
            item => item.Kind == HarnessEventKind.ReconciliationResolved
                    && item.SafeCode == "applied");
    }

    [Fact]
    public async Task Unknown_continue_resumes_evaluation_but_denies_mutation_until_one_read_only_receipt()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway(ToolAccessKind.Mutation)
        {
            ForcedReceipt = CancelledReceipt(),
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var coordinator = Coordinator(model, gateway, store);
        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        var executedBeforeGate = gateway.ExecuteAttempts.Count;

        var resolved = await coordinator.ApplyReconciliationAsync(
            RunId(),
            ReconciliationCommand.UnknownContinue,
            CancellationToken.None);
        Assert.Equal(HarnessRunState.Running, resolved.State);

        // The next model step proposes a MUTATION call: the broker-side gate must deny it
        // with a typed inspection requirement before any adapter dispatch happens.
        model.Enqueue(ToolStep());
        var mutationAttempt = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Null(mutationAttempt.ErrorCode);
        var denied = Assert.Single(mutationAttempt.Receipts);
        Assert.Equal(ToolExecutionOutcome.Failed, denied.Outcome);
        Assert.Equal("inspection_required", denied.Code);
        Assert.Equal(executedBeforeGate, gateway.ExecuteAttempts.Count);

        // A read-only inspection receipt is the only way to clear the gate: it executes
        // for real (that is what makes it a durable inspection)...
        gateway.PreparedKind = ToolAccessKind.ReadOnly;
        gateway.ForcedReceipt = null;
        model.Enqueue(ToolStep());
        var inspection = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Null(inspection.ErrorCode);
        var inspected = Assert.Single(inspection.Receipts);
        Assert.NotEqual("inspection_required", inspected.Code);
        Assert.Equal(executedBeforeGate + 1, gateway.ExecuteAttempts.Count);

        // ...after which normal evaluation resumes and mutations execute again.
        gateway.PreparedKind = null;
        model.Enqueue(ToolStep());
        var resumed = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Null(resumed.ErrorCode);
        Assert.Equal(executedBeforeGate + 2, gateway.ExecuteAttempts.Count);
    }

    // ------------------------------------------------------------------
    // Fixtures.
    // ------------------------------------------------------------------

    private static ToolExecutionReceipt CancelledReceipt() =>
        new(
            ExecutionId(),
            ToolCallId(),
            ToolExecutionOutcome.Cancelled,
            "process_cancelled",
            null,
            sideEffectsPossible: true);

    private static AgentRunCoordinator Coordinator(
        FakeModelStepRunner model,
        FakeToolGateway gateway,
        InMemoryHarnessEventStore store,
        ReconciliationInspectionGate? gate = null) =>
        new(
            model,
            gateway,
            store,
            new AgentContextAssembler(new EmptyContextSource()),
            new SequenceIdGenerator(),
            new IncrementingTimeProvider(Epoch),
            new ToolCallScheduler(),
            new TransientFrameBuffer(32),
            null,
            null,
            gate ?? new ReconciliationInspectionGate(),
            promptContextProvider: new FakeSystemPromptContextProvider());

    private static AssistantStep ToolStep() =>
        new(
            "checking",
            new[]
            {
                new ModelToolCall(
                    "provider-call",
                    "files.read",
                    "1",
                    JsonDocument.Parse("{\"path\":\"README.md\"}").RootElement.Clone()),
            },
            AssistantFinishReason.ToolCalls,
            16);

    private static AgentRun Run() =>
        new(
            RunId(),
            new SessionId("11111111111111111111111111111111"),
            new WorkspaceId("ws_11111111111111111111111111111111"),
            new HarnessRunStatus(HarnessRunState.Running),
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

    private static RunId RunId() => new("run_11111111111111111111111111111111");

    private static ExecutionId ExecutionId() => new("exec_11111111111111111111111111111111");

    private static ToolCallId ToolCallId() => new("call_11111111111111111111111111111111");

    private sealed class EmptyContextSource : IModelContextSource
    {
        public Task<ModelContextSourceSnapshot> LoadAsync(
            RunId runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ModelContextSourceSnapshot(
                Array.Empty<ModelContextEntry>(),
                Array.Empty<ModelContextEntry>()));
    }

    private sealed class IncrementingTimeProvider : ITimeProvider
    {
        private DateTimeOffset _current;

        public IncrementingTimeProvider(DateTimeOffset current) => _current = current;

        public DateTimeOffset UtcNow => _current = _current.AddSeconds(1);
    }

    private sealed class SequenceIdGenerator : IIdGenerator
    {
        private int _next;

        public RunId NewRunId() => new(Value("run_"));
        public ModelStepId NewModelStepId() => new(Value("step_"));
        public ToolCallId NewToolCallId() => new(Value("call_"));
        public ExecutionId NewExecutionId() => new(Value("exec_"));
        public ApprovalId NewApprovalId() => new(Value("apr_"));
        public TrustRequestId NewTrustRequestId() => new(Value("trust_"));
        public PolicyRuleId NewPolicyRuleId() => new(Value("rule_"));
        public EventId NewEventId() => new(Value("evt_"));
        public PayloadId NewPayloadId() => new(Value("payload_"));

        private string Value(string prefix) =>
            prefix + Interlocked.Increment(ref _next).ToString("x32");
    }
}
