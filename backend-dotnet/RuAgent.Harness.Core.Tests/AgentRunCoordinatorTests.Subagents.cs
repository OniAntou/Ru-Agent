using RuAgent.Harness.Core.Tests.Fakes;
using RuAgent.Harness.Core.Persistence;
using System.Text.Json;
using RuAgent.Harness.Core.Subagents;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed partial class AgentRunCoordinatorTests
{
    [Fact]
    public async Task Child_cannot_bypass_delegated_role_through_host_ask_user_interception()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var model = new FakeModelStepRunner();
        model.Enqueue(AskUserStep("Choose a target", "one", "two"));
        var gateway = new FakeToolGateway();
        var result = await Coordinator(model, gateway, store, internalRun: true).RunOneStepAsync(RunId(), default);
        Assert.Equal(HarnessRunState.Running, result.Run.State);
        Assert.Equal("subagent_ask_user_denied", Assert.Single(result.Receipts).Code);
        Assert.Empty(gateway.Proposals);
        Assert.Empty(gateway.ExecuteAttempts);
        Assert.DoesNotContain(store.Commits.SelectMany(commit => commit.Events), item => item.Kind == HarnessEventKind.InputRequested);
        Assert.Empty(store.Commits.SelectMany(commit => commit.ConversationOutbox));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exhausted_transient_provider_failure_is_terminal_for_child_and_preserves_root_continue(bool child)
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var result = await Coordinator(new StableThrowingModelStepRunner("provider_timeout"), new FakeToolGateway(), store,
            internalRun: child).RunOneStepAsync(RunId(), default);
        Assert.Equal(child ? HarnessRunState.Failed : HarnessRunState.WaitingUser, result.Run.State);
        Assert.Equal("provider_timeout", result.ErrorCode);
        Assert.Equal(1, result.Run.ModelStepsInWindow);
        Assert.Empty(store.Commits.SelectMany(commit => commit.ConversationOutbox));
        Assert.DoesNotContain(store.Commits.SelectMany(commit => commit.Events), item => child && item.Kind == HarnessEventKind.ContinueRequested);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_child_drains_inbox_before_loading_run_and_invoking_model(bool child)
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var inbox = new AdvancingInbox(store);
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("done"));
        var result = await Coordinator(model, new FakeToolGateway(), store,
            internalRun: child, subagentInbox: inbox).RunOneStepAsync(RunId(), default);
        Assert.Equal(child ? 1 : 0, inbox.Calls);
        Assert.Equal(child ? HarnessRunState.Completed : HarnessRunState.WaitingHistory, result.Run.State);
    }

    private sealed class AdvancingInbox(InMemoryHarnessEventStore store) : ISubagentInbox
    {
        public int Calls { get; private set; }
        public async Task<int> DeliverMessagesAsync(RunId runId, DateTimeOffset now, CancellationToken ct)
        {
            Calls++;
            var run = (await store.GetRunAsync(runId, ct))!;
            await store.CommitAsync(SubagentInboxDelivery.CreateCommit(run,
                System.Text.Encoding.UTF8.GetBytes("follow-up"), 1, now), ct);
            return 1;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Family_budget_is_reserved_before_dispatch_and_only_root_can_wait_for_continue(bool child, bool modelGate)
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        var gateway = new FakeToolGateway();
        var coordinator = Coordinator(model, gateway, store, internalRun: child, familyBudget: new RejectingBudget(modelGate));
        var result = await coordinator.RunOneStepAsync(RunId(), default);
        Assert.Equal(child ? HarnessRunState.Failed : HarnessRunState.WaitingUser, result.Run.State);
        Assert.Equal("subagent_family_budget_exhausted", result.ErrorCode);
        Assert.Equal(modelGate ? 0 : 1, model.Requests.Count);
        Assert.Empty(gateway.ExecuteAttempts);
        Assert.Equal(0, result.Run.ToolCallsInWindow);
    }

    private sealed class RejectingBudget(bool modelGate) : IAgentFamilyBudget
    {
        public Task<bool> ReserveModelAsync(AgentRun run, ModelStepId step, CancellationToken ct) => Task.FromResult(!modelGate);
        public Task<bool> ReserveToolsAsync(AgentRun run, ModelStepId step, int count, CancellationToken ct) => Task.FromResult(false);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Child_attempt_limits_fail_without_dispatch_or_user_continue(bool modelLimit)
    {
        var original = Run(new(HarnessRunState.Running));
        var run = new AgentRun(original.RunId, original.SessionId, original.WorkspaceId, original.Status,
            original.Version, original.LastSequence, 1, modelLimit ? 32 : 0, modelLimit ? 0 : 64,
            original.Preset, original.PolicySnapshotVersion, original.PolicySnapshotSha256, original.CreatedAt, original.UpdatedAt);
        var store = new InMemoryHarnessEventStore();
        store.Seed(run);
        var model = new FakeModelStepRunner();
        model.Enqueue(modelLimit ? FinalStep("should not run") : ToolStep());
        var gateway = new FakeToolGateway();
        var result = await Coordinator(model, gateway, store, internalRun: true).RunOneStepAsync(RunId(), default);
        Assert.Equal(HarnessRunState.Failed, result.Run.State);
        Assert.Equal(modelLimit ? "subagent_model_step_limit" : "subagent_tool_call_limit", result.ErrorCode);
        Assert.Equal(modelLimit ? 0 : 1, model.Requests.Count);
        Assert.Empty(gateway.ExecuteAttempts);
        Assert.Empty(store.Commits.SelectMany(commit => commit.ConversationOutbox));
    }

    [Fact]
    public async Task Deferred_wait_parks_without_fabricating_a_completed_tool_receipt()
    {
        var model = new FakeModelStepRunner();
        using var arguments = JsonDocument.Parse("{\"agent_ids\":[\"agent_one\"],\"timeout_ms\":30000}");
        model.Enqueue(new AssistantStep("waiting", [new("provider_wait", "agent_wait", "1.0.0", arguments.RootElement)],
            AssistantFinishReason.ToolCalls, 16));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var gateway = new FakeToolGateway
        {
            ForcedReceipt = ToolExecutionReceipt.DeferredSubagentWait(new("exec_" + new string('1', 32)),
                new("call_" + new string('1', 32))),
        };
        var coordinator = Coordinator(model, gateway, store);
        var result = await coordinator.RunOneStepAsync(RunId(), default);
        Assert.Equal(HarnessRunState.WaitingSubagents, result.Run.State);
        Assert.Empty(result.Receipts);
        var commit = Assert.Single(store.Commits, c => c.SubagentWait is not null);
        Assert.Equal(gateway.ExecuteAttempts.Single(), commit.SubagentWait!.ExecutionId);
        Assert.Equal("provider_wait", commit.SubagentWait.ProviderCallId);
        Assert.Equal(30000, commit.SubagentWait.TimeoutMs);
        Assert.DoesNotContain(store.Commits.SelectMany(c => c.Events), e => e.Kind == HarnessEventKind.ToolExecutionCompleted);
        var parked = await coordinator.RunOneStepAsync(RunId(), default);
        Assert.Equal("run_not_runnable", parked.ErrorCode);
        Assert.Single(model.Requests);
        Assert.Single(gateway.ExecuteAttempts);
    }

    [Fact]
    public async Task Wait_mixed_with_mutation_is_rejected_before_either_dispatches()
    {
        var model = new FakeModelStepRunner();
        using var wait = JsonDocument.Parse("{\"agent_ids\":[\"agent_one\"]}");
        using var write = JsonDocument.Parse("{\"op\":\"write\",\"path\":\"x.txt\",\"content\":\"x\"}");
        model.Enqueue(new AssistantStep("mixed", [new("provider_wait", "agent_wait", "1.0.0", wait.RootElement),
            new("provider_write", "write", "1.0.0", write.RootElement)], AssistantFinishReason.ToolCalls, 16));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var gateway = new FakeToolGateway();
        var result = await Coordinator(model, gateway, store).RunOneStepAsync(RunId(), default);
        Assert.Empty(gateway.Proposals);
        Assert.Empty(gateway.ExecuteAttempts);
        Assert.Equal(2, result.Receipts.Count);
        Assert.All(result.Receipts, receipt => Assert.Equal("subagent_wait_must_be_alone", receipt.Code));
        Assert.Equal(HarnessRunState.Running, result.Run.State);
    }

    [Theory]
    [InlineData("delegated result", HarnessRunState.Completed)]
    [InlineData("[BLOCKED] cannot inspect the missing input", HarnessRunState.Failed)]
    public async Task Internal_child_result_never_enters_user_history(string text, HarnessRunState expected)
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep(text));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store, internalRun: true);
        var result = await coordinator.RunOneStepAsync(RunId(), default);
        Assert.Equal(expected, result.Run.State);
        Assert.Empty(store.Commits.SelectMany(c => c.ConversationOutbox));
    }
}
