using System.Text.Json;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Core.Subagents;

/// <summary>Maps integration uncertainty into the existing native mutation-reconciliation path.</summary>
public static class SubagentApplyReceipt
{
    public static ToolExecutionReceipt Create(ToolProposal proposal, PreparedToolExecution prepared, SubagentApplyOutcome outcome)
    {
        if (proposal.ToolId != "agent_apply" || proposal.ToolVersion != "1.0.0"
            || !SubagentContractValidator.Validate(proposal.ToolId, proposal.Arguments).IsValid
            || prepared.ToolCallId != proposal.ToolCallId || prepared.ModelOrder != proposal.ModelOrder
            || prepared.AccessKind != ToolAccessKind.Mutation || !Enum.IsDefined(outcome.Status)
            || outcome.AppliedFiles is < 0 or > SubagentLimits.MaxSnapshotFiles
            || outcome.Status == SubagentApplyStatus.Conflict && outcome.AppliedFiles != 0)
            throw new InvalidOperationException("subagent_apply_receipt_invalid");
        var uncertain = outcome.Status is SubagentApplyStatus.Pending or SubagentApplyStatus.Partial or SubagentApplyStatus.Unknown;
        var code = uncertain ? "subagent_apply_reconciliation_required" : outcome.Code;
        var content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            agent_id = proposal.Arguments.GetProperty("agent_id").GetString(),
            result_version = proposal.Arguments.GetProperty("result_version").GetInt64(),
            status = outcome.Status == SubagentApplyStatus.Pending ? "unknown" : outcome.Status.ToString().ToLowerInvariant(),
            applied_files = outcome.AppliedFiles,
            code,
        });
        return new(prepared.ExecutionId, prepared.ToolCallId,
            uncertain ? ToolExecutionOutcome.OutcomeUnknown : outcome.Status == SubagentApplyStatus.Applied
                ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed,
            code, null, uncertain || outcome.AppliedFiles > 0, content, HarnessPayloadEncoding.Json, proposal.ProviderCallId);
    }
}
