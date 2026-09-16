using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class AgentActivityLifecycleTests
{
    private static readonly DateTimeOffset Epoch =
        new(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Run_publishes_thinking_and_tool_activity_without_persisting_activity()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var sink = new RecordingSink();
        var coordinator = Coordinator(model, new FakeToolGateway(), store, sink);

        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Collection(
            sink.Items,
            thinkingStarted =>
            {
                Assert.Equal(AgentActivityKind.Thinking, thinkingStarted.Activity.Kind);
                Assert.Equal(AgentActivityState.Started, thinkingStarted.Activity.State);
            },
            thinkingCompleted =>
            {
                Assert.Equal(AgentActivityKind.Thinking, thinkingCompleted.Activity.Kind);
                Assert.Equal(AgentActivityState.Completed, thinkingCompleted.Activity.State);
                Assert.True(thinkingCompleted.Activity.DurationMs > 0);
            },
            toolStarted =>
            {
                Assert.Equal(AgentActivityKind.Analyze, toolStarted.Activity.Kind);
                Assert.Equal(AgentActivityState.Started, toolStarted.Activity.State);
            },
            toolCompleted =>
            {
                Assert.Equal(AgentActivityKind.Analyze, toolCompleted.Activity.Kind);
                Assert.Equal(AgentActivityState.Completed, toolCompleted.Activity.State);
                Assert.True(toolCompleted.Activity.DurationMs > 0);
            });

        Assert.DoesNotContain(
            store.Commits.SelectMany(commit => commit.Events),
            item => item.Kind.ToString().Contains("Activity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Approval_wait_changes_tool_activity_to_waiting_without_claiming_success()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway
        {
            DecisionFactory = _ => ToolProposalDecision.Ask(
                ToolAccessKind.Mutation,
                new ApprovalId("apr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var sink = new RecordingSink();
        var coordinator = Coordinator(model, gateway, store, sink);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingApproval, result.Run.State);
        var toolItems = sink.Items
            .Where(item => item.Activity.Kind == AgentActivityKind.Analyze)
            .Select(item => item.Activity)
            .ToArray();
        Assert.Equal(
            new[] { AgentActivityState.Started, AgentActivityState.Waiting },
            toolItems.Select(item => item.State));
        Assert.DoesNotContain(toolItems, item => item.State == AgentActivityState.Completed);
    }

    [Fact]
    public async Task Denied_tool_activity_is_failed_and_does_not_expose_arguments()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway
        {
            DecisionFactory = _ => ToolProposalDecision.Deny(
                ToolAccessKind.ReadOnly,
                "policy_denied"),
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var sink = new RecordingSink();
        var coordinator = Coordinator(model, gateway, store, sink);

        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        var failed = Assert.Single(
            sink.Items.Where(item => item.Activity.Kind == AgentActivityKind.Analyze),
            item => item.Activity.State == AgentActivityState.Failed);
        Assert.Equal("policy_denied", failed.Activity.SafeCode);
        Assert.Equal("README.md", failed.Activity.FileName);
        Assert.DoesNotContain("arguments", failed.Activity.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_only_transport_retry_publishes_a_retry_activity_before_succeeding()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway(transportFailures: 1);
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var sink = new RecordingSink();
        var coordinator = Coordinator(model, gateway, store, sink);

        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        var retry = Assert.Single(
            sink.Items,
            item => item.Activity.Kind == AgentActivityKind.Retry).Activity;
        Assert.Equal(AgentActivityState.Completed, retry.State);
        Assert.Equal("retrying", retry.SafeCode);
        Assert.Equal(2, retry.Count);
        Assert.Equal("README.md", retry.FileName);
        Assert.Equal(2, gateway.ExecuteAttempts.Count);
        Assert.Contains(
            sink.Items,
            item => item.Activity.Kind == AgentActivityKind.Analyze
                && item.Activity.State == AgentActivityState.Completed);
    }

    [Fact]
    public async Task Resume_executes_stashed_proposal_without_new_model_step()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var sink = new RecordingSink();
        var approval = new ApprovalId("apr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var evaluations = 0;
        var gateway = new FakeToolGateway
        {
            DecisionFactory = _ =>
            {
                evaluations++;
                return evaluations == 1
                    ? ToolProposalDecision.Ask(ToolAccessKind.Mutation, approval)
                    : ToolProposalDecision.Allow(ToolAccessKind.ReadOnly, "canonical-resumed");
            },
        };
        var coordinator = Coordinator(model, gateway, store, sink);

        var asked = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Equal("approval_required", asked.ErrorCode);

        await coordinator.ResumeAfterApprovalAsync(RunId(), CancellationToken.None);

        var resumed = await coordinator.TryResumeRetainedApprovalAsync(RunId(), CancellationToken.None);
        Assert.NotNull(resumed);
        Assert.Null(resumed.ErrorCode);
        Assert.Equal(2, evaluations);
        Assert.Single(model.Requests);
        Assert.Single(gateway.ExecuteAttempts);
        var receipt = Assert.Single(resumed.Receipts);
        Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
        Assert.Contains(
            store.Commits.SelectMany(commit => commit.Events),
            item => item.Kind == HarnessEventKind.ToolExecutionRequested);
    }

    [Fact]
    public async Task Stale_stash_dropped_without_resolution()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var sink = new RecordingSink();
        var gateway = new FakeToolGateway
        {
            DecisionFactory = _ => ToolProposalDecision.Ask(
                ToolAccessKind.Mutation,
                new ApprovalId("apr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
        };
        var coordinator = Coordinator(model, gateway, store, sink);

        var asked = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Equal("approval_required", asked.ErrorCode);

        // Still waiting: refuse and keep nothing pending on the gateway.
        Assert.Null(await coordinator.TryResumeRetainedApprovalAsync(RunId(), CancellationToken.None));
        Assert.Empty(gateway.ExecuteAttempts);

        // Running again but without the approval marker: stale stash drops.
        await CommitStateAsync(store, HarnessRunState.Running, "continued");
        Assert.Null(await coordinator.TryResumeRetainedApprovalAsync(RunId(), CancellationToken.None));
        Assert.Empty(gateway.ExecuteAttempts);

        // The next model step proceeds normally; nothing executes behind its back.
        model.Enqueue(ToolStep());
        gateway.DecisionFactory = _ => ToolProposalDecision.Allow(ToolAccessKind.ReadOnly, "canonical-second");
        var next = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Null(next.ErrorCode);
        Assert.Equal(2, model.Requests.Count);
        Assert.Single(gateway.ExecuteAttempts);
    }

    private static async Task CommitStateAsync(
        InMemoryHarnessEventStore store,
        HarnessRunState state,
        string safeCode)
    {
        var run = await store.GetRunAsync(RunId(), CancellationToken.None);
        Assert.NotNull(run);
        var next = new AgentRun(
            run.RunId,
            run.SessionId,
            run.WorkspaceId,
            new HarnessRunStatus(state),
            run.Version + 1,
            run.LastSequence + 1,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            run.Preset,
            run.PolicySnapshotVersion,
            run.PolicySnapshotSha256,
            run.CreatedAt,
            Epoch,
            run.TerminalAt,
            run.TerminalErrorCode);
        var committed = await store.CommitAsync(
            new HarnessCommit(
                RunId(),
                run.Version,
                next,
                new[]
                {
                    new HarnessEventIntent(
                        new EventId("evt_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
                        HarnessEventKind.RunStateChanged,
                        safeCode: safeCode),
                },
                Array.Empty<HarnessPayloadIntent>(),
                Array.Empty<ConversationOutboxIntent>()),
            CancellationToken.None);
        Assert.Equal(HarnessCommitOutcome.Committed, committed.Outcome);
    }

    [Fact]
    public async Task Mutation_transport_failure_retries_once_then_succeeds()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(MutationToolStep());
        var gateway = new FakeToolGateway(ToolAccessKind.Mutation, transportFailures: 1);
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var sink = new RecordingSink();
        var coordinator = Coordinator(model, gateway, store, sink);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Equal(2, gateway.ExecuteAttempts.Count);
        var receipt = Assert.Single(result.Receipts);
        Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
        Assert.Contains(
            sink.Items,
            item => item.Activity.Kind == AgentActivityKind.Retry
                && item.Activity.State == AgentActivityState.Completed);
        Assert.Contains(
            sink.Items,
            item => item.Activity.Kind == AgentActivityKind.Edit
                && item.Activity.State == AgentActivityState.Completed);
    }

    [Fact]
    public async Task Mutation_transport_failure_twice_reports_unknown_without_looping()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(MutationToolStep());
        var gateway = new FakeToolGateway(ToolAccessKind.Mutation, transportFailures: 2);
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var sink = new RecordingSink();
        var coordinator = Coordinator(model, gateway, store, sink);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(2, gateway.ExecuteAttempts.Count);
        var receipt = Assert.Single(result.Receipts);
        Assert.Equal(ToolExecutionOutcome.OutcomeUnknown, receipt.Outcome);
        Assert.Equal("transport_failure", receipt.Code);
    }

    private static AssistantStep MutationToolStep() =>
        new(
            "editing",
            new[]
            {
                new ModelToolCall(
                    "provider-call-edit",
                    "edit",
                    "1",
                    JsonDocument.Parse("{\"path\":\"Notes.txt\",\"old_string\":\"a\",\"new_string\":\"b\"}").RootElement.Clone()),
            },
            AssistantFinishReason.ToolCalls,
            16);

    [Fact]
    public async Task Inspection_gate_deny_publishes_waiting_reason_activity()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(MutationToolStep());
        var gateway = new FakeToolGateway(ToolAccessKind.Mutation);
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var sink = new RecordingSink();
        var gate = new ReconciliationInspectionGate();
        gate.Activate(RunId());
        var coordinator = Coordinator(model, gateway, store, sink, inspectionGate: gate);

        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        var reason = Assert.Single(
            sink.Items,
            item => item.Activity.Kind == AgentActivityKind.Status
                && item.Activity.State == AgentActivityState.Waiting);
        Assert.Equal("inspection_required", reason.Activity.SafeCode);
        Assert.Contains("Continue", reason.Activity.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconciliation_required_publishes_waiting_reason_activity()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(MutationToolStep());
        var gateway = new FakeToolGateway(ToolAccessKind.Mutation)
        {
            ForcedReceipt = new ToolExecutionReceipt(
                new ExecutionId("exec_22222222222222222222222222222222"),
                new ToolCallId("call_22222222222222222222222222222222"),
                ToolExecutionOutcome.Cancelled,
                "process_cancelled",
                null,
                sideEffectsPossible: true),
        };
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var sink = new RecordingSink();
        var coordinator = Coordinator(model, gateway, store, sink);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.ReconciliationRequired, result.Run.State);
        var reason = Assert.Single(
            sink.Items,
            item => item.Activity.Kind == AgentActivityKind.Status
                && item.Activity.State == AgentActivityState.Waiting);
        Assert.Equal("reconciliation_required", reason.Activity.SafeCode);
        Assert.Contains("paused", reason.Activity.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static AgentRunCoordinator Coordinator(
        FakeModelStepRunner model,
        FakeToolGateway gateway,
        InMemoryHarnessEventStore store,
        IAgentActivitySink sink,
        ReconciliationInspectionGate? inspectionGate = null) =>
        new(
            model,
            gateway,
            store,
            new AgentContextAssembler(new EmptyContextSource()),
            new SequenceIdGenerator(),
            new IncrementingTimeProvider(Epoch),
            new ToolCallScheduler(),
            new TransientFrameBuffer(32),
            inspectionGate: inspectionGate,
            activitySink: sink,
            promptContextProvider: new FakeSystemPromptContextProvider());

    private static AssistantStep ToolStep() =>
        new(
            "checking",
            new[]
            {
                new ModelToolCall(
                    "provider-call",
                    "read",
                    "1",
                    JsonDocument.Parse("{\"op\":\"read\",\"path\":\"README.md\"}").RootElement.Clone()),
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

    private sealed class RecordingSink : IAgentActivitySink
    {
        public List<(RunId RunId, AgentActivity Activity)> Items { get; } = new();

        public void Publish(RunId runId, AgentActivity activity) => Items.Add((runId, activity));
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
