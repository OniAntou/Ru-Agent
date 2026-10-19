using System.Text.Json;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Core.Tests.Fakes;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed partial class AgentRunCoordinatorTests
{
    [Theory]
    [InlineData(SubagentApplyStatus.Applied, ToolExecutionOutcome.Succeeded, true)]
    [InlineData(SubagentApplyStatus.Conflict, ToolExecutionOutcome.Failed, false)]
    [InlineData(SubagentApplyStatus.Partial, ToolExecutionOutcome.OutcomeUnknown, true)]
    [InlineData(SubagentApplyStatus.Unknown, ToolExecutionOutcome.OutcomeUnknown, true)]
    [InlineData(SubagentApplyStatus.Pending, ToolExecutionOutcome.OutcomeUnknown, true)]
    public void Apply_receipt_preserves_native_identity_and_never_labels_ambiguity_as_ordinary_failure(
        SubagentApplyStatus status, ToolExecutionOutcome expected, bool sideEffects)
    {
        var (proposal, prepared) = ApplyIdentity();
        var count = status == SubagentApplyStatus.Conflict ? 0 : 1;
        var receipt = SubagentApplyReceipt.Create(proposal, prepared, new(status, count, "fixture_outcome"));
        Assert.Equal(expected, receipt.Outcome);
        Assert.Equal(sideEffects, receipt.SideEffectsPossible);
        Assert.Equal(prepared.ExecutionId, receipt.ExecutionId);
        Assert.Equal(proposal.ToolCallId, receipt.ToolCallId);
        Assert.Equal(proposal.ProviderCallId, receipt.ProviderCallId);
        Assert.Equal(HarnessPayloadEncoding.Json, receipt.ContentEncoding);
        using var output = JsonDocument.Parse(receipt.ModelContent);
        Assert.Equal("agent_fixture", output.RootElement.GetProperty("agent_id").GetString());
        Assert.Equal(1, output.RootElement.GetProperty("result_version").GetInt64());
        Assert.Equal(count, output.RootElement.GetProperty("applied_files").GetInt32());
        if (expected == ToolExecutionOutcome.OutcomeUnknown)
            Assert.Equal("subagent_apply_reconciliation_required", receipt.Code);
    }

    [Theory]
    [InlineData(SubagentApplyStatus.Partial)]
    [InlineData(SubagentApplyStatus.Unknown)]
    public async Task Ambiguous_apply_receipt_parks_parent_in_existing_durable_reconciliation_state(SubagentApplyStatus status)
    {
        var (proposal, prepared) = ApplyIdentity();
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new(HarnessRunState.Running)));
        var model = new FakeModelStepRunner();
        model.Enqueue(new AssistantStep("applying", [new(proposal.ProviderCallId, proposal.ToolId, proposal.ToolVersion, proposal.Arguments)],
            AssistantFinishReason.ToolCalls, 16));
        var gateway = new FakeToolGateway(ToolAccessKind.Mutation)
            { ForcedReceipt = SubagentApplyReceipt.Create(proposal, prepared, new(status, 1, "fixture_unknown")) };
        var result = await Coordinator(model, gateway, store).RunOneStepAsync(RunId(), default);
        Assert.Equal(HarnessRunState.ReconciliationRequired, result.Run.State);
        Assert.Equal(ToolExecutionOutcome.OutcomeUnknown, Assert.Single(result.Receipts).Outcome);
        Assert.Single(gateway.ExecuteAttempts);
        Assert.Single(model.Requests);
        Assert.Single(store.Commits.SelectMany(c => c.Events), e => e.Kind == HarnessEventKind.ReconciliationRequired);
        Assert.False(AgentRunStateMachine.TryTransition(result.Run.Status, HarnessRunTransition.Continue).Accepted);
        Assert.Empty(store.Commits.SelectMany(c => c.ConversationOutbox));
    }

    private static (ToolProposal, PreparedToolExecution) ApplyIdentity()
    {
        var arguments = JsonSerializer.SerializeToElement(new { agent_id = "agent_fixture", result_version = 1 });
        var proposal = new ToolProposal(RunId(), new("step_" + new string('a', 32)), new("call_" + new string('b', 32)),
            "provider-apply", 1, "agent_apply", "1.0.0", arguments);
        var prepared = new PreparedToolExecution(new("exec_" + new string('c', 32)), proposal.ToolCallId,
            1, ToolAccessKind.Mutation, "fixture_preparation", [], 1);
        return (proposal, prepared);
    }
}
