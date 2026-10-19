using RuAgent.Harness.Core.Policy;

namespace RuAgent.Harness.Core;

internal sealed partial class AgentToolExecutionPipeline
{
    private sealed class ProposalBatch
    {
        internal ProposalBatch(int capacity)
        {
            Proposals = new(capacity);
            Prepared = new(capacity);
            Approvals = new(capacity);
        }

        internal ProposalBatch(IReadOnlyList<ToolProposal> proposals) : this(proposals.Count)
        {
            foreach (var proposal in proposals) Proposals.Add(proposal.ModelOrder, proposal);
            RejectWaitBatch = proposals.Count > 1 && proposals.Any(proposal => proposal.ToolId == "agent_wait");
        }

        internal Dictionary<int, ToolProposal> Proposals { get; }
        internal List<PreparedToolExecution> Prepared { get; }
        internal Dictionary<int, DeniedToolExecution> Denied { get; } = new();
        internal List<AskDecision> Approvals { get; }
        internal List<InputAsk> Questions { get; } = new();
        internal bool RejectWaitBatch { get; init; }
    }

    private async Task EvaluateBatchProposalAsync(
        AgentRun run, ToolProposal proposal, ProposalBatch batch, CancellationToken cancellationToken)
    {
        if (batch.RejectWaitBatch)
        {
            batch.Denied.Add(proposal.ModelOrder, CreateDeniedExecution(proposal, _ids.NewExecutionId(),
                "subagent_wait_must_be_alone", "Call agent_wait alone in a tool batch."));
            return;
        }
        if (proposal.ToolId is AskUserToolId)
        {
            if (!_allowUserInput)
            {
                batch.Denied.Add(proposal.ModelOrder, CreateDeniedExecution(proposal, _ids.NewExecutionId(),
                    "subagent_ask_user_denied", "Report the blocker to the parent agent instead of asking the user directly."));
                return;
            }
            HandleAskUserProposal(proposal, batch.Denied, batch.Questions);
            return;
        }

        var decision = await EvaluateProposalAsync(run, proposal, batch.Prepared, batch.Denied, cancellationToken)
            .ConfigureAwait(false);
        if (decision.Decision == PolicyDecision.Ask)
        {
            if (decision.ApprovalId is null) throw new InvalidOperationException("approval_id_missing");
            batch.Approvals.Add(new AskDecision(proposal, decision));
        }
    }

    private async Task<AgentToolPipelineResult> ResumeBatchAsync(
        AgentRun run, ModelStepId stepId, IReadOnlyList<ToolProposal> proposals, CancellationToken cancellationToken)
    {
        var batch = new ProposalBatch(proposals);
        try
        {
            // Preparation tokens from before a wait cannot be reused. Every retained
            // proposal is evaluated and prepared against current execution authority.
            foreach (var proposal in proposals)
                await EvaluateBatchProposalAsync(run, proposal, batch, cancellationToken).ConfigureAwait(false);
            return await CompleteBatchAsync(run, stepId, batch, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await AbortBatchAsync(batch).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<AgentToolPipelineResult> CompleteBatchAsync(
        AgentRun run, ModelStepId stepId, ProposalBatch batch, CancellationToken cancellationToken,
        bool preserveWaitingState = false)
    {
        if (!preserveWaitingState && batch.Questions.Count > 0)
        {
            await DiscardPreparedAsync(batch.Prepared).ConfigureAwait(false);
            batch.Prepared.Clear();
            await RevokeAllApprovalsAsync(batch.Approvals, cancellationToken).ConfigureAwait(false);
            return await PauseForInputAsync(run, stepId, batch.Questions[0], batch.Proposals, batch.Denied,
                cancellationToken).ConfigureAwait(false);
        }

        if (batch.Approvals.Count > 0 || preserveWaitingState)
        {
            await RevokeExtraApprovalsAsync(batch.Approvals, cancellationToken).ConfigureAwait(false);
            if (batch.Approvals.Count == 0) throw new InvalidOperationException("retained_approval_gate_missing");
            await DiscardPreparedAsync(batch.Prepared).ConfigureAwait(false);
            batch.Prepared.Clear();
            return await PauseForApprovalAsync(run, stepId, batch.Approvals[0], batch.Proposals, batch.Denied,
                cancellationToken, preserveWaitingState: preserveWaitingState).ConfigureAwait(false);
        }

        return await CompleteExecutionsAsync(run, stepId, batch.Prepared, batch.Proposals, batch.Denied,
            new List<(int ModelOrder, ToolExecutionReceipt Receipt)>(), cancellationToken).ConfigureAwait(false);
    }

    private async Task AbortBatchAsync(ProposalBatch batch)
    {
        // Cleanup must never replace the original failure while unwinding a batch.
        await RevokeApprovalsBestEffortAsync(batch.Approvals).ConfigureAwait(false);
        await DiscardPreparedAsync(batch.Prepared).ConfigureAwait(false);
    }
}
