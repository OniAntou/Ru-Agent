using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol.Subagents;

namespace RuBackend.Harness.Subagents;

public sealed partial class SubagentApplyService
{
    public async Task<ToolExecutionReceipt> ApplyReceiptAsync(ToolProposal proposal, PreparedToolExecution prepared, CancellationToken ct)
    {
        if (prepared.ToolCallId != proposal.ToolCallId || prepared.ModelOrder != proposal.ModelOrder
            || prepared.AccessKind != ToolAccessKind.Mutation)
            throw new HarnessStoreException("subagent_apply_receipt_invalid");
        SubagentApplyOutcome outcome;
        try
        {
            outcome = await ApplyAsync(proposal, prepared.PreparationToken, prepared.GatewayGeneration, ct).ConfigureAwait(false);
        }
        catch (SubagentApplyInterruptedException error)
        {
            // The final durable status was lost; never downgrade a possibly written
            // result to an ordinary failure or retryable transport interruption.
            outcome = error.Outcome;
        }
        catch (OperationCanceledException)
        {
            // Cancellation may arrive after a file write. The native pipeline already
            // parks cancelled mutations with possible effects in reconciliation.
            return new(prepared.ExecutionId, prepared.ToolCallId, ToolExecutionOutcome.Cancelled,
                "subagent_apply_cancelled", null, true, providerCallId: proposal.ProviderCallId);
        }
        return SubagentApplyReceipt.Create(proposal, prepared, outcome);
    }
}
