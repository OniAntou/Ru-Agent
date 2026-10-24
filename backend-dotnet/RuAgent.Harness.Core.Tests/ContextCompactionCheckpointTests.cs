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
    public async Task Format_repair_cannot_dispatch_after_first_call_spends_remaining_budget()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(AgentBudgetPolicy.ModelStepLimit - 1));
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
    public async Task Checkpoint_never_dispatches_when_budget_is_spent()
    {
        var store = new InMemoryHarnessEventStore();
        var run = Run(AgentBudgetPolicy.ModelStepLimit);
        store.Seed(run);
        var model = new CheckpointModel(store);
        var result = await CheckpointCoordinator(model, store).CompactAsync(RunId(), ContextCompactionStrategy.Normal, ContextCompactionReason.Manual, default);
        Assert.Equal("model_step_budget_exhausted", result.ErrorCode);
        Assert.Equal(HarnessRunState.WaitingUser, result.Run!.State);
        Assert.Equal(0, model.Calls);
        Assert.DoesNotContain(store.Commits.SelectMany(commit => commit.Events), evt => evt.SafeCode == "checkpoint_started");
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
        store.Seed(Run(AgentBudgetPolicy.ModelStepLimit));
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
            return Task.FromResult(new ContextCheckpointResult(text, new ModelUsage(100, 50, 150, null, null), ErrorCode));
        }
        public Task<ModelStepResult> RunStepAsync(ModelStepRequest request, Func<AssistantDelta, ValueTask> delta, CancellationToken ct) =>
            throw new InvalidOperationException("Compaction must not invoke the execution loop.");
    }
}
