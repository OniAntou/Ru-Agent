using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;
using RuAgent.Protocol.Harness;

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
    public async Task Cancelled_side_effecting_tool_continues_behind_the_inspection_gate()
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
        var gate = new ReconciliationInspectionGate();
        var coordinator = Coordinator(model, gateway, store, gate);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        var receipt = Assert.Single(result.Receipts);
        Assert.Equal(ToolExecutionOutcome.Cancelled, receipt.Outcome);
        Assert.True(receipt.SideEffectsPossible);
        Assert.Equal(HarnessRunState.Running, result.Run.State);
        Assert.False(gate.IsMutationDenied(RunId()));
        Assert.DoesNotContain(store.Commits.SelectMany(commit => commit.Events),
            item => item.Kind == HarnessEventKind.RunNoteAdded);
        Assert.DoesNotContain(store.Commits.SelectMany(commit => commit.Events),
            item => item.Kind == HarnessEventKind.ReconciliationRequired);
    }

    [Fact]
    public async Task Timed_out_side_effecting_tool_continues_behind_the_inspection_gate()
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
        Assert.Equal(HarnessRunState.Running, result.Run.State);
        Assert.DoesNotContain(store.Commits.SelectMany(commit => commit.Events),
            item => item.Kind == HarnessEventKind.RunNoteAdded);
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
    // Fixtures.
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // Durable gate projection: the gate must survive a host restart.
    // ------------------------------------------------------------------

    [Fact]
    public void Gate_projection_is_disarmed_before_any_reconciliation()
    {
        Assert.False(ReconciliationGateProjection.IsMutationDenied(
            [Event("run.state_changed", safeCode: "running")]));
    }

    [Fact]
    public void Gate_projection_arms_on_unknown_continue()
    {
        Assert.True(ReconciliationGateProjection.IsMutationDenied(
            [Event("reconciliation.resolved", safeCode: ReconciliationGateProjection.UnknownContinueCode)]));
    }

    [Theory]
    [InlineData("applied")]
    [InlineData("not_applied")]
    public void Gate_projection_stays_disarmed_for_a_direct_resolution(string resolution)
    {
        Assert.False(ReconciliationGateProjection.IsMutationDenied(
            [Event("reconciliation.resolved", safeCode: resolution)]));
    }

    [Fact]
    public void Gate_projection_clears_on_the_recorded_inspection()
    {
        Assert.False(ReconciliationGateProjection.IsMutationDenied(
        [
            Event("reconciliation.resolved", safeCode: ReconciliationGateProjection.UnknownContinueCode),
            Event("tool.execution.completed", safeCode: "read_only"),
            Event("reconciliation.resolved", safeCode: ReconciliationGateProjection.InspectionRecordedCode),
        ]));
    }

    [Fact]
    public void Gate_projection_rearms_when_a_later_resume_is_again_unknown()
    {
        // Two ambiguous outcomes in one run: the second unknown_continue must arm
        // the gate again even though the first inspection released it.
        Assert.True(ReconciliationGateProjection.IsMutationDenied(
        [
            Event("reconciliation.resolved", safeCode: ReconciliationGateProjection.UnknownContinueCode),
            Event("reconciliation.resolved", safeCode: ReconciliationGateProjection.InspectionRecordedCode),
            Event("reconciliation.resolved", safeCode: ReconciliationGateProjection.UnknownContinueCode),
        ]));
    }

    [Fact]
    public void Gate_projection_ignores_a_read_only_receipt_that_is_not_the_recorded_inspection()
    {
        // Ordinary read-only work must not look like the inspection: only the
        // recorded resolution releases the gate.
        Assert.True(ReconciliationGateProjection.IsMutationDenied(
        [
            Event("reconciliation.resolved", safeCode: ReconciliationGateProjection.UnknownContinueCode),
            Event("tool.execution.completed", safeCode: "read_only"),
            Event("tool.execution.completed", safeCode: "read_only"),
        ]));
    }

    [Fact]
    public async Task Clearing_the_gate_is_recorded_durably_so_it_can_be_rebuilt()
    {
        // Arm the gate, then run one succeeded read-only call: the same commit must
        // carry the durable marker the projection reads back.
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway(ToolAccessKind.ReadOnly)
        {
            ForcedReceipt = new ToolExecutionReceipt(
                ExecutionId(),
                ToolCallId(),
                ToolExecutionOutcome.Succeeded,
                "read_only",
                null,
                sideEffectsPossible: false),
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var gate = new ReconciliationInspectionGate();
        gate.Activate(RunId());
        var coordinator = Coordinator(model, gateway, store, gate);

        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.False(gate.IsMutationDenied(RunId()));
        var inspection = Assert.Single(
            store.Commits.SelectMany(commit => commit.Events),
            item => item.Kind == HarnessEventKind.ReconciliationResolved
                && item.SafeCode == ReconciliationGateProjection.InspectionRecordedCode);
        Assert.NotNull(inspection.ModelStepId);
        Assert.NotNull(inspection.ToolCallId);
        Assert.NotNull(inspection.ExecutionId);
    }

    private static HarnessEvent Event(
        string kind,
        string? safeCode = null,
        ToolExecutionOutcome? outcome = null) =>
        new(
            new EventId($"evt_{Guid.NewGuid():N}"),
            RunId(),
            1,
            kind,
            Epoch,
            safeCode: safeCode,
            toolOutcome: outcome,
            sideEffectsPossible: outcome is null ? null : false);

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
        public InputId NewInputId() => new(Value("inp_"));
        public TrustRequestId NewTrustRequestId() => new(Value("trust_"));
        public PolicyRuleId NewPolicyRuleId() => new(Value("rule_"));
        public EventId NewEventId() => new(Value("evt_"));
        public PayloadId NewPayloadId() => new(Value("payload_"));

        private string Value(string prefix) =>
            prefix + Interlocked.Increment(ref _next).ToString("x32");
    }
}
