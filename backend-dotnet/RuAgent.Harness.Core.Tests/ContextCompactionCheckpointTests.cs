using System.Text;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed partial class ContextCompactionCoordinatorTests
{
    [Fact]
    public async Task Structured_checkpoint_is_admitted_billed_and_committed_without_dispatching_tools()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var model = new CheckpointModel(store);
        var coordinator = CheckpointCoordinator(model, store);
        var result = await coordinator.CompactAsync(RunId(), ContextCompactionStrategy.Normal, ContextCompactionReason.Manual, default);
        Assert.Equal(ContextCompactionOutcome.Committed, result.Outcome);
        Assert.Equal(1, result.Run!.ModelStepsInWindow);
        Assert.Equal(1, model.Calls);
        Assert.DoesNotContain(model.Request!.Context.Entries, entry => entry.SourceId == "current");
        var commit = Assert.Single(store.Commits, commit => commit.ContextCompaction is not null);
        var summary = Encoding.UTF8.GetString(commit.Payloads.Single().Plaintext.Span);
        Assert.Contains("keep Vietnamese responses", summary);
        Assert.Contains("untrusted=true", summary);
        Assert.Contains(store.Commits.SelectMany(commit => commit.Events), evt => evt.Kind == HarnessEventKind.ModelUsageRecorded);
        Assert.DoesNotContain(store.Commits.SelectMany(commit => commit.Events), evt => evt.Kind == HarnessEventKind.ToolExecutionRequested);
    }

    [Fact]
    public async Task Malformed_checkpoint_gets_one_durable_format_repair_call_before_compaction()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var model = new CheckpointModel(store) { MalformedFirst = true };
        var result = await CheckpointCoordinator(model, store).CompactAsync(
            RunId(), ContextCompactionStrategy.Normal, ContextCompactionReason.Manual, default);

        Assert.Equal(ContextCompactionOutcome.Committed, result.Outcome);
        Assert.Equal(2, result.Run!.ModelStepsInWindow);
        Assert.Equal(2, model.Calls);
        Assert.Contains(ContextCheckpoint.RepairInstruction, string.Join("\n", model.Requests[1].Context.Entries.Select(entry => entry.Content)));
        Assert.Equal(2, store.Commits.SelectMany(commit => commit.Events).Count(evt => evt.SafeCode == "checkpoint_started"));
        Assert.Equal(2, store.Commits.SelectMany(commit => commit.Events).Count(evt => evt.Kind == HarnessEventKind.ModelUsageRecorded));
        Assert.Contains(store.Commits, commit => commit.ContextCompaction is not null);
    }

    [Fact]
    public async Task Format_repair_opens_an_automatic_window_when_the_first_call_spends_the_budget()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(AgentBudgetPolicy.ModelStepLimit - 1));
        var model = new CheckpointModel(store) { MalformedFirst = true };
        var result = await CheckpointCoordinator(model, store).CompactAsync(
            RunId(), ContextCompactionStrategy.Normal, ContextCompactionReason.Manual, default);

        Assert.Equal(ContextCompactionOutcome.Committed, result.Outcome);
        Assert.Null(result.ErrorCode);
        Assert.Equal(2, result.Run!.BudgetWindow);
        Assert.Equal(1, result.Run.ModelStepsInWindow);
        Assert.Equal(2, model.Calls);
        Assert.Contains(store.Commits, commit => commit.ContextCompaction is not null);
    }

    [Fact]
    public async Task Format_repair_cannot_dispatch_after_the_automatic_window_cap()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(
            AgentBudgetPolicy.ModelStepLimit - 1,
            budgetWindow: AgentBudgetPolicy.AutomaticWindowLimit));
        var model = new CheckpointModel(store) { MalformedFirst = true };
        var result = await CheckpointCoordinator(model, store).CompactAsync(
            RunId(), ContextCompactionStrategy.Normal, ContextCompactionReason.Manual, default);

        Assert.Equal("model_step_budget_exhausted", result.ErrorCode);
        Assert.Equal(HarnessRunState.WaitingUser, result.Run!.State);
        Assert.Equal(AgentBudgetPolicy.ModelStepLimit, result.Run.ModelStepsInWindow);
        Assert.Equal(1, model.Calls);
        Assert.DoesNotContain(store.Commits, commit => commit.ContextCompaction is not null);
    }

    [Theory]
    [InlineData("checkpoint_invalid")]
    [InlineData("checkpoint_incomplete")]
    [InlineData("checkpoint_tool_rejected")]
    public async Task Failed_checkpoint_preserves_history_and_spent_budget(string code)
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var model = new CheckpointModel(store) { ErrorCode = code };
        var result = await CheckpointCoordinator(model, store).CompactAsync(RunId(), ContextCompactionStrategy.Normal, ContextCompactionReason.Manual, default);
        Assert.Equal(code, result.ErrorCode);
        var calls = code == "checkpoint_invalid" ? 2 : 1;
        Assert.Equal(calls, result.Run!.ModelStepsInWindow);
        Assert.Equal(calls, model.Calls);
        Assert.DoesNotContain(store.Commits, commit => commit.ContextCompaction is not null);
        Assert.Contains(store.Commits.SelectMany(commit => commit.Events), evt => evt.Kind == HarnessEventKind.ModelUsageRecorded);
    }

    [Fact]
    public async Task Checkpoint_opens_an_automatic_window_at_the_step_limit()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(AgentBudgetPolicy.ModelStepLimit));
        var model = new CheckpointModel(store);
        var result = await CheckpointCoordinator(model, store).CompactAsync(
            RunId(), ContextCompactionStrategy.Normal, ContextCompactionReason.Manual, default);

        Assert.Equal(ContextCompactionOutcome.Committed, result.Outcome);
        Assert.Null(result.ErrorCode);
        Assert.Equal(2, result.Run!.BudgetWindow);
        Assert.Equal(1, result.Run.ModelStepsInWindow);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task Checkpoint_never_dispatches_when_the_automatic_window_cap_is_reached()
    {
        var store = new InMemoryHarnessEventStore();
        var run = Run(
            AgentBudgetPolicy.ModelStepLimit,
            budgetWindow: AgentBudgetPolicy.AutomaticWindowLimit);
        store.Seed(run);
        var model = new CheckpointModel(store);
        var result = await CheckpointCoordinator(model, store).CompactAsync(RunId(), ContextCompactionStrategy.Normal, ContextCompactionReason.Manual, default);
        Assert.Equal("model_step_budget_exhausted", result.ErrorCode);
        Assert.Equal(HarnessRunState.WaitingUser, result.Run!.State);
        Assert.Equal(0, model.Calls);
        Assert.DoesNotContain(store.Commits.SelectMany(commit => commit.Events), evt => evt.SafeCode == "checkpoint_started");
    }

    [Fact]
    public async Task Automatic_window_continues_the_step_without_asking_for_continue()
    {
        // The first window is spent, but a root run opens the next one itself: the
        // step must run, not park, and no continue.requested event may be written.
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(AgentBudgetPolicy.ModelStepLimit));
        var model = new FakeModelStepRunner();
        model.Enqueue(new AssistantStep(
            "done",
            Array.Empty<ModelToolCall>(),
            AssistantFinishReason.Stop,
            estimatedInputTokens: 16));
        var snapshot = Snapshot(ModelContextEntry.Text(
            "current",
            1,
            ModelContextRole.User,
            "current request"));
        var coordinator = Coordinator(
            model,
            store,
            new ReloadingSource(snapshot, snapshot),
            limitTokens: 2_000);

        var result = await coordinator.RunOneStepAsync(RunId(), default);

        Assert.Null(result.ErrorCode);
        Assert.Equal(2, result.Run!.BudgetWindow);
        Assert.Equal(1, result.Run.ModelStepsInWindow);
        Assert.Equal(HarnessRunState.WaitingHistory, result.Run.State);
        Assert.Single(model.Requests);
        Assert.DoesNotContain(
            store.Commits.SelectMany(commit => commit.Events),
            evt => evt.Kind == HarnessEventKind.ContinueRequested);
    }

    [Fact]
    public async Task Automatic_checkpoint_failure_stops_the_run_instead_of_repeating_calls()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var model = new CheckpointModel(store) { ErrorCode = "checkpoint_incomplete" };
        var result = await CheckpointCoordinator(model, store).RunOneStepAsync(RunId(), default);
        Assert.Equal(HarnessRunState.Failed, result.Run.State);
        Assert.Equal("checkpoint_incomplete", result.ErrorCode);
        Assert.Equal(1, model.Calls);
        Assert.DoesNotContain(store.Commits, commit => commit.ContextCompaction is not null);
    }

    [Fact]
    public async Task Automatic_budget_exhaustion_waits_for_continue_without_a_summary_call()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(
            AgentBudgetPolicy.ModelStepLimit,
            budgetWindow: AgentBudgetPolicy.AutomaticWindowLimit));
        var model = new CheckpointModel(store);
        var result = await CheckpointCoordinator(model, store).RunOneStepAsync(RunId(), default);
        Assert.Equal(HarnessRunState.WaitingUser, result.Run.State);
        Assert.Equal("model_step_budget_exhausted", result.ErrorCode);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Family_budget_denial_parks_the_root_before_dispatch()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var model = new CheckpointModel(store);
        var result = await CheckpointCoordinator(model, store, familyBudget: new DeniedFamilyBudget())
            .CompactAsync(RunId(), ContextCompactionStrategy.Normal, ContextCompactionReason.Manual, default);
        Assert.Equal(HarnessRunState.WaitingUser, result.Run!.State);
        Assert.Equal("subagent_family_budget_exhausted", result.ErrorCode);
        Assert.Equal(0, model.Calls);
        Assert.DoesNotContain(store.Commits.SelectMany(commit => commit.Events), evt => evt.SafeCode == "checkpoint_started");
    }

    [Fact]
    public async Task Child_budget_exhaustion_is_terminal_without_a_summary_call()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(RuAgent.Protocol.Subagents.SubagentLimits.MaxModelSteps));
        var model = new CheckpointModel(store);
        var result = await CheckpointCoordinator(model, store, internalRun: true)
            .CompactAsync(RunId(), ContextCompactionStrategy.Normal, ContextCompactionReason.Manual, default);
        Assert.Equal(HarnessRunState.Failed, result.Run!.State);
        Assert.Equal("subagent_model_step_limit", result.ErrorCode);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Checkpoint_calls_stay_capped_when_an_automatic_window_resets_the_counter()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(AgentBudgetPolicy.ModelStepLimit - 1));
        var codes = new Queue<string?>();
        // Each round asks for a format repair and then an input-too-large span,
        // which is two admitted calls. Three rounds would be six calls; the
        // documented four-call allowance must stop it, even though the second
        // call opens an automatic window and resets ModelStepsInWindow.
        for (var round = 0; round < 3; round++)
        {
            codes.Enqueue("checkpoint_invalid");
            codes.Enqueue("checkpoint_input_too_large");
        }
        var model = new CheckpointModel(store) { ErrorCodes = codes };
        // Several similarly sized spans, so halving the covered budget really
        // changes the plan and the coordinator takes its retry path more than once.
        var source = new ReloadingSource(
            Snapshot(
                ModelContextEntry.Text("h1", 1, ModelContextRole.User, new string('a', 3000)),
                ModelContextEntry.Text("h2", 2, ModelContextRole.User, new string('b', 3000)),
                ModelContextEntry.Text("h3", 3, ModelContextRole.User, new string('c', 3000)),
                ModelContextEntry.Text("current", 4, ModelContextRole.User, "current request")),
            Snapshot());
        var coordinator = new AgentRunCoordinator(
            model,
            new FakeToolGateway(),
            store,
            new AgentContextAssembler(source),
            new SequenceIdGenerator(),
            new IncrementingTimeProvider(Epoch),
            new ToolCallScheduler(),
            new TransientFrameBuffer(32),
            contextLimit: new StaticModelContextLimitProvider(4000),
            promptContextProvider: new FakeSystemPromptContextProvider());

        var result = await coordinator.CompactAsync(
            RunId(), ContextCompactionStrategy.Normal, ContextCompactionReason.Manual, default);

        Assert.Equal("checkpoint_input_too_large", result.ErrorCode);
        Assert.True(model.Calls <= 4, $"admitted {model.Calls} checkpoint calls");
        Assert.Equal(2, result.Run!.BudgetWindow);
    }

    [Theory]
    [InlineData("checkpoint_timeout", false)]
    [InlineData("checkpoint_provider_transient", false)]
    [InlineData("checkpoint_timeout", true)]
    [InlineData("checkpoint_provider_transient", true)]
    public async Task Transient_checkpoint_failure_parks_the_root_but_never_asks_a_child_user(string code, bool child)
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var model = new CheckpointModel(store) { ErrorCode = code };
        var result = await CheckpointCoordinator(model, store, internalRun: child).RunOneStepAsync(RunId(), default);
        Assert.Equal(child ? HarnessRunState.Failed : HarnessRunState.WaitingUser, result.Run.State);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(1, result.Run.ModelStepsInWindow);
        Assert.Equal(1, model.Calls);
        Assert.DoesNotContain(store.Commits, commit => commit.ContextCompaction is not null);
    }

    private static AgentRunCoordinator CheckpointCoordinator(CheckpointModel model, InMemoryHarnessEventStore store,
        Subagents.IAgentFamilyBudget? familyBudget = null, bool internalRun = false) =>
        new(model, new FakeToolGateway(), store, new AgentContextAssembler(new ReloadingSource(
                Snapshot(ModelContextEntry.Text("history", 1, ModelContextRole.User, new string('a', 20000)),
                    ModelContextEntry.Text("current", 2, ModelContextRole.User, "current request")), Snapshot())),
            new SequenceIdGenerator(), new IncrementingTimeProvider(Epoch), new ToolCallScheduler(), new TransientFrameBuffer(32),
            contextLimit: new StaticModelContextLimitProvider(4000), promptContextProvider: new FakeSystemPromptContextProvider(),
            familyBudget: familyBudget, internalRun: internalRun);

    private sealed class DeniedFamilyBudget : Subagents.IAgentFamilyBudget
    {
        public Task<bool> ReserveModelAsync(AgentRun run, ModelStepId step, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> ReserveToolsAsync(AgentRun run, ModelStepId step, int count, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CheckpointModel(InMemoryHarnessEventStore store) : IModelStepRunner, IContextCheckpointSummarizer
    {
        public int Calls { get; private set; }
        public ModelStepRequest? Request { get; private set; }
        public List<ModelStepRequest> Requests { get; } = [];
        public string? ErrorCode { get; init; }
        public Queue<string?>? ErrorCodes { get; init; }
        public bool MalformedFirst { get; init; }
        public Task<ContextCheckpointResult> SummarizeAsync(ModelStepRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Contains(store.Commits.SelectMany(commit => commit.Events), evt => evt.SafeCode == "checkpoint_started");
            Calls++;
            Request = request;
            Requests.Add(request);
            var text = string.Join("\n", ContextCheckpoint.Sections.Select(section => section + "\n- keep Vietnamese responses"));
            if (MalformedFirst && Calls == 1) text = "## malformed";
            var code = ErrorCodes is { Count: > 0 } ? ErrorCodes.Dequeue() : ErrorCode;
            return Task.FromResult(new ContextCheckpointResult(text, new ModelUsage(100, 50, 150, null, null), code));
        }
        public Task<ModelStepResult> RunStepAsync(ModelStepRequest request, Func<AssistantDelta, ValueTask> delta, CancellationToken ct) =>
            throw new InvalidOperationException("Compaction must not invoke the execution loop.");
    }
}
