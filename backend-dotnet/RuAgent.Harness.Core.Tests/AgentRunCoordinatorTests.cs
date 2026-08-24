using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class AgentRunCoordinatorTests
{
    private static readonly DateTimeOffset Epoch =
        new(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task User_history_ack_gates_first_step_and_final_history_ack_gates_completion()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("done"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(
            HarnessRunState.WaitingHistory,
            HistoryWaitKind.UserBeforeFirstStep)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store);

        var blockedBeforeUserHistory = await coordinator.RunOneStepAsync(
            RunId(),
            CancellationToken.None);
        Assert.Equal("run_not_runnable", blockedBeforeUserHistory.ErrorCode);
        Assert.Empty(model.Requests);

        var running = await coordinator.AcknowledgeHistoryAsync(
            RunId(),
            HistoryWaitKind.UserBeforeFirstStep,
            CancellationToken.None);
        Assert.Equal(HarnessRunState.Running, running.State);

        var final = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Null(final.ErrorCode);
        Assert.Equal(HarnessRunState.WaitingHistory, final.Run.State);
        Assert.Equal(HistoryWaitKind.FinalBeforeCompletion, final.Run.HistoryWaitKind);
        Assert.Single(model.Requests);
        var finalCommit = Assert.Single(
            store.Commits,
            commit => commit.ConversationOutbox.Count == 1);
        Assert.Single(finalCommit.Payloads);

        var blockedBeforeFinalHistory = await coordinator.RunOneStepAsync(
            RunId(),
            CancellationToken.None);
        Assert.Equal("run_not_runnable", blockedBeforeFinalHistory.ErrorCode);
        Assert.Single(model.Requests);

        var completed = await coordinator.AcknowledgeHistoryAsync(
            RunId(),
            HistoryWaitKind.FinalBeforeCompletion,
            CancellationToken.None);
        Assert.Equal(HarnessRunState.Completed, completed.State);
    }

    [Fact]
    public async Task Preparation_payload_and_execution_request_commit_before_dispatch()
    {
        var trace = new List<string>();
        var model = new FakeModelStepRunner(trace);
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway(trace: trace)
        {
            PreparationPayloads = new[]
            {
                new PreparedPayload(
                    HarnessPayloadKind.RecoveryOpaque,
                    HarnessPayloadAudience.InternalOnly,
                    HarnessPayloadEncoding.Opaque,
                    64,
                    new byte[] { 0, 1, 2 }),
            },
        };
        var store = new InMemoryHarnessEventStore(trace);
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Running, result.Run.State);
        var modelCompletedCommit = Assert.Single(
            store.Commits,
            commit => commit.Events.Any(item =>
                item.Kind == HarnessEventKind.ModelStepCompleted));
        Assert.Equal(
            HarnessPayloadKind.AssistantText,
            Assert.Single(modelCompletedCommit.Payloads).Kind);
        var proposedCommit = Assert.Single(
            store.Commits,
            commit => commit.Events.Any(item => item.Kind == HarnessEventKind.ToolProposed));
        var arguments = Assert.Single(proposedCommit.Payloads);
        Assert.Equal(HarnessPayloadKind.ToolArguments, arguments.Kind);
        Assert.Equal(arguments.PayloadId, Assert.Single(proposedCommit.Events).PayloadId);
        using (var document = JsonDocument.Parse(arguments.Plaintext))
        {
            Assert.Equal("provider-call", document.RootElement.GetProperty("provider_call_id").GetString());
            Assert.Equal("files.read", document.RootElement.GetProperty("tool_id").GetString());
            Assert.Equal("1", document.RootElement.GetProperty("tool_version").GetString());
            Assert.Equal(
                "README.md",
                document.RootElement.GetProperty("arguments").GetProperty("path").GetString());
        }
        var requestedCommit = Assert.Single(
            store.Commits,
            commit => commit.Events.Any(item =>
                item.Kind == HarnessEventKind.ToolExecutionRequested));
        Assert.Single(requestedCommit.Payloads);
        Assert.True(trace.IndexOf("prepare") < trace.IndexOf("commit:ToolExecutionRequested"));
        Assert.True(trace.IndexOf("commit:ToolExecutionRequested") < trace.IndexOf("execute"));
        Assert.True(trace.IndexOf("execute") < trace.IndexOf("commit:ToolExecutionCompleted"));
        var receiptCommit = Assert.Single(
            store.Commits,
            commit => commit.Events.Any(item =>
                item.Kind == HarnessEventKind.ToolExecutionCompleted));
        var resultPayload = Assert.Single(receiptCommit.Payloads);
        Assert.Equal(HarnessPayloadKind.ToolOutput, resultPayload.Kind);
        Assert.Equal(
            resultPayload.PayloadId,
            Assert.Single(result.Receipts).ResultPayloadId);
        Assert.True(Assert.Single(result.Receipts).ModelContent.IsEmpty);
    }

    [Fact]
    public async Task Receipt_is_durable_before_the_next_model_step()
    {
        var trace = new List<string>();
        var model = new FakeModelStepRunner(trace);
        model.Enqueue(ToolStep());
        model.Enqueue(FinalStep("done"));
        var store = new InMemoryHarnessEventStore(trace);
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(trace: trace), store);

        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.True(trace.IndexOf("commit:ToolExecutionCompleted") < trace.IndexOf("model:2"));
        Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task Read_only_transport_retry_uses_same_execution_id_and_at_most_two_attempts()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway(
            ToolAccessKind.ReadOnly,
            transportFailures: 1);
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Running, result.Run.State);
        Assert.Equal(2, gateway.ExecuteAttempts.Count);
        Assert.Single(gateway.ExecuteAttempts.Distinct());
        Assert.Equal(ToolExecutionOutcome.Succeeded, Assert.Single(result.Receipts).Outcome);
    }

    [Fact]
    public async Task Repeated_read_only_transport_failure_stops_after_second_attempt()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway(
            ToolAccessKind.ReadOnly,
            transportFailures: 3);
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(2, gateway.ExecuteAttempts.Count);
        Assert.Single(gateway.ExecuteAttempts.Distinct());
        Assert.Equal(ToolExecutionOutcome.Failed, Assert.Single(result.Receipts).Outcome);
        Assert.Equal(HarnessRunState.Running, result.Run.State);
    }

    [Fact]
    public async Task Mutation_transport_failure_is_not_retried_and_requires_reconciliation()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway(
            ToolAccessKind.Mutation,
            transportFailures: 1);
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Single(gateway.ExecuteAttempts);
        var receipt = Assert.Single(result.Receipts);
        Assert.Equal(ToolExecutionOutcome.OutcomeUnknown, receipt.Outcome);
        Assert.True(receipt.SideEffectsPossible);
        Assert.Equal(HarnessRunState.ReconciliationRequired, result.Run.State);
        Assert.Contains(store.Commits.SelectMany(commit => commit.Events), item =>
            item.Kind == HarnessEventKind.ReconciliationRequired);

        var blocked = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Equal("run_not_runnable", blocked.ErrorCode);
        Assert.Single(gateway.ExecuteAttempts);
    }

    [Fact]
    public async Task Full_transient_buffer_drops_frames_without_blocking_durable_final_commit()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("done"), "a", "b", "c");
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var frames = new TransientFrameBuffer(1);
        var coordinator = Coordinator(model, new FakeToolGateway(), store, frames);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(HarnessRunState.WaitingHistory, result.Run.State);
        Assert.Equal(2, frames.DroppedCount);
        var dropped = Assert.Single(
            store.Commits.SelectMany(commit => commit.Events),
            item => item.Kind == HarnessEventKind.TransientFramesDropped);
        Assert.Equal(2, dropped.SafeCount);
        var persisted = new List<HarnessEventEnvelope>();
        await foreach (var envelope in store.ReadEventsAsync(
                           RunId(),
                           0,
                           CancellationToken.None))
        {
            persisted.Add(envelope);
        }

        var persistedDropped = Assert.Single(
            persisted,
            envelope => envelope.Event.Kind == "transient.frames.dropped");
        Assert.Equal(2, persistedDropped.Event.SafeCount);
    }

    private static AgentRunCoordinator Coordinator(
        FakeModelStepRunner model,
        FakeToolGateway gateway,
        InMemoryHarnessEventStore store,
        TransientFrameBuffer? frames = null) =>
        new(
            model,
            gateway,
            store,
            new AgentContextAssembler(new EmptyContextSource()),
            new SequenceIdGenerator(),
            new IncrementingTimeProvider(Epoch),
            new ToolCallScheduler(),
            frames ?? new TransientFrameBuffer(32));

    private static AssistantStep FinalStep(string text) =>
        new(text, Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, 16);

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

    private static RunId RunId() =>
        new("run_11111111111111111111111111111111");

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
