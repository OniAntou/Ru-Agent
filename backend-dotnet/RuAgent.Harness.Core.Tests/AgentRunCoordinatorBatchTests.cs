using System.Text.Json;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed partial class AgentRunCoordinatorTests
{
    [Theory]
    [InlineData("approval")]
    [InlineData("input")]
    [InlineData("refresh")]
    public async Task Retained_batch_abort_revokes_new_approvals_and_discards_new_preparations(string entry)
    {
        var model = new FakeModelStepRunner();
        var gateway = new FakeToolGateway();
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, gateway, store);
        if (entry != "input")
        {
            model.Enqueue(ToolStep(callCount: 3));
            var original = new ApprovalId("apr_" + new string('a', 32));
            gateway.DecisionFactory = proposal => proposal.ModelOrder == 2
                ? ToolProposalDecision.Ask(ToolAccessKind.Mutation, original)
                : ToolProposalDecision.Allow(ToolAccessKind.ReadOnly, "original-" + proposal.ModelOrder);
            await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
            if (entry == "approval")
                await coordinator.ResumeAfterApprovalAsync(RunId(), CancellationToken.None);
        }

        gateway.RevokedApprovals.Clear();
        gateway.CancelAttempts.Clear();
        var fresh = new ApprovalId("apr_" + new string('b', 32));
        gateway.DecisionFactory = proposal => proposal.ModelOrder switch
        {
            1 => ToolProposalDecision.Allow(ToolAccessKind.ReadOnly, "fresh-canonical"),
            2 => ToolProposalDecision.Ask(ToolAccessKind.Mutation, fresh),
            _ => throw new InvalidOperationException("evaluation_failed"),
        };

        AgentRunStepResult? result;
        if (entry == "input")
        {
            var stepId = new ModelStepId("step_" + new string('1', 32));
            using var arguments = JsonDocument.Parse("{\"path\":\"README.md\"}");
            var proposals = Enumerable.Range(1, 3).Select(order => new ToolProposal(
                RunId(), stepId, new ToolCallId("call_" + new string((char)('0' + order), 32)),
                "provider-" + order, order, "read", "1.0.0", arguments.RootElement.Clone())).ToArray();
            result = await coordinator.TryResumeRetainedInputAsync(RunId(),
                new RetainedInputStep(proposals, new InputId("inp_" + new string('1', 32)), stepId),
                CancellationToken.None);
        }
        else if (entry == "approval")
            result = await coordinator.TryResumeRetainedApprovalAsync(RunId(), CancellationToken.None);
        else
            result = await coordinator.TryRefreshRetainedApprovalAsync(RunId(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(HarnessRunState.Failed, result!.Run.State);
        Assert.Equal(fresh, Assert.Single(gateway.RevokedApprovals));
        Assert.Single(gateway.CancelAttempts);
        Assert.Empty(gateway.ExecuteAttempts);
    }
}
