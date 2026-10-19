using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol.Subagents;

namespace RuBackend.Harness.Subagents;

public sealed partial class SubagentApplyService
{
    public async Task<SubagentApplyInspection> InspectAsync(ToolProposal proposal, CancellationToken ct)
    {
        await _operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var frozen = await LoadAsync(proposal, ct, inspection: true).ConfigureAwait(false);
            var state = await journal.ReadAsync(frozen.ParentRunId, frozen.AgentId, frozen.ResultVersion, ct).ConfigureAwait(false)
                ?? throw new HarnessStoreException("subagent_apply_journal_missing");
            return await gateways.InspectSubagentApplyAsync(proposal, frozen, state, ct).ConfigureAwait(false);
        }
        finally { _operation.Release(); }
    }
}
