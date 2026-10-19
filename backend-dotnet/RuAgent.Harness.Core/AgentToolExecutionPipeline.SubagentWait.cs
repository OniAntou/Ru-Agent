using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;

namespace RuAgent.Harness.Core;

internal sealed partial class AgentToolExecutionPipeline
{
    private async Task<AgentToolPipelineResult> ParkSubagentWaitAsync(AgentRun run, ModelStepId stepId,
        IReadOnlyList<PreparedToolExecution> prepared, IReadOnlyDictionary<int, ToolProposal> proposals,
        IReadOnlyDictionary<int, DeniedToolExecution> denied, IReadOnlyList<ToolExecutionReceipt> receipts, CancellationToken ct)
    {
        if (prepared.Count != 1 || proposals.Count != 1 || denied.Count != 0 || receipts.Count != 1)
            throw new InvalidOperationException("subagent_wait_batch_invalid");
        var call = prepared[0];
        var proposal = proposals[call.ModelOrder];
        var receipt = receipts[0];
        if (proposal.ToolId != "agent_wait" || proposal.ToolVersion != "1.0.0" || call.AccessKind != ToolAccessKind.ReadOnly
            || receipt.ExecutionId != call.ExecutionId || receipt.ToolCallId != call.ToolCallId
            || receipt.SideEffectsPossible || receipt.ResultPayloadId is not null || !receipt.ModelContent.IsEmpty)
            throw new InvalidOperationException("subagent_wait_receipt_invalid");
        var intent = new SubagentWaitIntent(stepId, proposal.ToolCallId, call.ExecutionId, proposal.ProviderCallId, proposal.Arguments);
        var transition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.WaitForSubagents);
        if (!transition.Accepted) throw new InvalidOperationException(transition.ErrorCode);
        var next = run.Advance(transition.Next, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1, _time.UtcNow);
        var commit = new HarnessCommit(run.RunId, run.Version, next,
            [new(_ids.NewEventId(), HarnessEventKind.RunStateChanged, modelStepId: stepId, safeCode: "waiting_subagents")],
            [], [], subagentWait: intent);
        var result = await _store.CommitAsync(commit, ct).ConfigureAwait(false);
        return result.Outcome switch
        {
            HarnessCommitOutcome.Committed => new(result.Run!, [], "subagents_pending"),
            HarnessCommitOutcome.VersionConflict => throw new InvalidOperationException("run_version_conflict"),
            _ => throw new InvalidOperationException("run_not_found"),
        };
    }
}
