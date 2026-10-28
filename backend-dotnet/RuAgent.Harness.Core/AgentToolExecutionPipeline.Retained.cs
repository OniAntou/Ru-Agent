using RuAgent.Harness.Core.Policy;

namespace RuAgent.Harness.Core;

internal sealed partial class AgentToolExecutionPipeline
{
    /// <summary>
    /// Continues a step parked by an approval wait without the model re-proposing.
    /// Stashed proposals are re-evaluated (the fresh approval token allows them)
    /// and prepared anew: pre-wait preparation tokens go stale across the wait.
    /// </summary>
    public async Task<AgentToolPipelineResult> ResumeRetainedAsync(
        AgentRun run,
        RetainedApprovalStep retained,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(retained);
        ValidateRetainedApprovalStep(run, retained);
        return await ResumeBatchAsync(run, retained.ModelStepId, retained.Proposals, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Continues a step parked by an <c>ask_user</c> wait without the model
    /// re-proposing. The asked call was already completed by the answer route,
    /// so the durable rehydrator excludes it; the remaining proposals are
    /// re-evaluated and prepared anew.
    /// </summary>
    public async Task<AgentToolPipelineResult> ResumeRetainedInputAsync(
        AgentRun run,
        RetainedInputStep retained,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(retained);
        ValidateRetainedInputStep(run, retained);
        return await ResumeBatchAsync(run, retained.ModelStepId, retained.Proposals, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<AgentToolPipelineResult> RefreshRetainedApprovalAsync(
        AgentRun run,
        RetainedApprovalStep retained,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(retained);
        if (run.State != HarnessRunState.WaitingApproval)
        {
            throw new InvalidOperationException("retained_approval_refresh_state_invalid");
        }

        ValidateRetainedApprovalStep(run, retained);
        var batch = new ProposalBatch(retained.Proposals);
        try
        {
            foreach (var proposal in retained.Proposals)
            {
                // Refresh never executes a batch or resolves an input question.
                var decision = await EvaluateProposalAsync(run, proposal, batch.Prepared, batch.Denied, cancellationToken)
                    .ConfigureAwait(false);
                if (decision.Decision == PolicyDecision.Ask)
                    batch.Approvals.Add(new AskDecision(proposal, decision));
            }

            return await CompleteBatchAsync(run, retained.ModelStepId, batch, cancellationToken,
                preserveWaitingState: true).ConfigureAwait(false);
        }
        catch
        {
            await AbortBatchAsync(batch).ConfigureAwait(false);
            throw;
        }
    }

    private static void ValidateRetainedApprovalStep(
        AgentRun run,
        RetainedApprovalStep retained)
    {
        var orders = retained.Proposals
            .Select(proposal => proposal.ModelOrder)
            .ToArray();
        if (retained.Proposals.Count == 0
            || retained.Proposals.Any(proposal =>
                proposal.RunId != run.RunId
                || proposal.ModelStepId != retained.ModelStepId)
            || orders.Distinct().Count() != orders.Length
            || retained.Proposals.Select(proposal => proposal.ProviderCallId)
                .Distinct(StringComparer.Ordinal)
                .Count() != retained.Proposals.Count
            || retained.Proposals.Select(proposal => proposal.ToolCallId)
                .Distinct()
                .Count() != retained.Proposals.Count
            || orders.Zip(orders.Skip(1), (left, right) => right > left).Any(isIncreasing => !isIncreasing))
        {
            throw new InvalidOperationException("retained_approval_invalid");
        }
    }

    private static void ValidateRetainedInputStep(
        AgentRun run,
        RetainedInputStep retained)
    {
        var orders = retained.Proposals
            .Select(proposal => proposal.ModelOrder)
            .ToArray();
        if (retained.Proposals.Count == 0
            || retained.Proposals.Any(proposal =>
                proposal.RunId != run.RunId
                || proposal.ModelStepId != retained.ModelStepId)
            || orders.Distinct().Count() != orders.Length
            || retained.Proposals.Select(proposal => proposal.ProviderCallId)
                .Distinct(StringComparer.Ordinal)
                .Count() != retained.Proposals.Count
            || retained.Proposals.Select(proposal => proposal.ToolCallId)
                .Distinct()
                .Count() != retained.Proposals.Count
            || orders.Zip(orders.Skip(1), (left, right) => right > left).Any(isIncreasing => !isIncreasing))
        {
            throw new InvalidOperationException("retained_input_invalid");
        }
    }
}
