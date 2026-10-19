using RuAgent.Harness.Core;
using RuAgent.Protocol.Subagents;

namespace RuBackend.Harness.Broker;

public sealed partial class InProcessToolGatewayFactory
{
    public Task<SubagentApplyInspection> InspectSubagentApplyAsync(ToolProposal proposal,
        SubagentChangeSet changes, SubagentApplyJournalState journal, CancellationToken ct) =>
        SubagentApplyGateway(proposal.RunId).InspectSubagentApplyAsync(proposal, changes, journal, ct);

    public Task<ToolProposalDecision> EvaluateSubagentApplyAsync(ToolProposal proposal,
        SubagentChangeSet changes, CancellationToken ct) =>
        SubagentApplyGateway(proposal.RunId).EvaluateSubagentApplyAsync(proposal, changes, ct);

    public Task<SubagentApplyOutcome> ApplySubagentChangesAsync(ToolProposal proposal, SubagentChangeSet changes,
        string canonicalIntentToken, long? gatewayGeneration, ISubagentApplyJournal journal, CancellationToken ct) =>
        SubagentApplyGateway(proposal.RunId).ApplySubagentChangesAsync(proposal, changes, canonicalIntentToken,
            gatewayGeneration, journal, ct);

    private InProcessToolGateway SubagentApplyGateway(RunId runId) =>
        _gateways.TryGetValue(runId.Value, out var gateway) && !gateway.IsDisposed
            ? gateway : throw new ToolTransportException("subagent_apply_root_not_open");
}
