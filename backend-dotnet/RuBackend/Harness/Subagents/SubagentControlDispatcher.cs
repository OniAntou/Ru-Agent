using System.Text.Json;
using RuAgent.Broker.Runtime;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;

namespace RuBackend.Harness.Subagents;

/// <summary>Durable orchestration only. A live Broker callback permission is required for dispatch.</summary>
public sealed class SubagentControlDispatcher(IHarnessEventStore events, SqliteSubagentStore agents,
    SubagentRunFactory children, SubagentCancellationCoordinator cancellation, SubagentApplyService apply)
{
    public Task<ToolProposalDecision> EvaluateApplyAsync(ToolProposal proposal, CancellationToken ct) => apply.EvaluateAsync(proposal, ct);
    public Task<ToolExecutionReceipt> ApplyAsync(ToolProposal proposal, PreparedToolExecution prepared, CancellationToken ct) =>
        apply.ApplyReceiptAsync(proposal, prepared, ct);

    /// <summary>Read-only metadata preflight; never grants execution authority.</summary>
    public async Task ValidateAsync(ToolProposal proposal, CancellationToken ct)
    {
        if (!SubagentContractValidator.IsControlTool(proposal.ToolId) || proposal.ToolVersion != "1.0.0"
            || !SubagentContractValidator.Validate(proposal.ToolId, proposal.Arguments).IsValid)
            throw new SubagentControlRejectedException("invalid_subagent_arguments");
        var root = await events.GetRunAsync(proposal.RunId, ct).ConfigureAwait(false);
        if (root is null || root.State != HarnessRunState.Running || await agents.IsInternalRunAsync(root.RunId, ct).ConfigureAwait(false))
            throw new SubagentControlRejectedException("subagent_root_inactive");
        if (proposal.ToolId == "agent_spawn") return;
        var owned = await agents.ListAsync(root.RunId, ct).ConfigureAwait(false);
        var ids = proposal.ToolId == "agent_wait"
            ? proposal.Arguments.GetProperty("agent_ids").EnumerateArray().Select(v => v.GetString()!).ToArray()
            : [proposal.Arguments.GetProperty("agent_id").GetString()!];
        foreach (var id in ids)
        {
            var child = owned.SingleOrDefault(a => a.AgentId == id)
                ?? throw new SubagentControlRejectedException("subagent_not_found");
            if (proposal.ToolId == "agent_send" && child.Canceled)
                throw new SubagentControlRejectedException("subagent_canceled");
        }
    }

    public async Task<ToolExecutionReceipt> DispatchAsync(AuthorizedSubagentControl intent,
        ToolProposal proposal, PreparedToolExecution prepared, CancellationToken ct)
    {
        intent.DemandActive();
        if (intent.RootRunId != proposal.RunId.Value || intent.InvocationId != proposal.ToolCallId.Value
            || intent.ToolId != proposal.ToolId || !JsonElement.DeepEquals(intent.Arguments, proposal.Arguments)
            || prepared.ToolCallId != proposal.ToolCallId || prepared.ModelOrder != proposal.ModelOrder)
            throw new SubagentControlRejectedException("subagent_control_binding_invalid");
        await ValidateAsync(proposal, ct).ConfigureAwait(false);
        var root = await events.GetRunAsync(proposal.RunId, ct).ConfigureAwait(false);
        if (root is null || root.SessionId.Value != intent.SessionId)
            throw new SubagentControlRejectedException("subagent_control_binding_invalid");
        var expectedKind = proposal.ToolId == "agent_wait" ? ToolAccessKind.ReadOnly : ToolAccessKind.Mutation;
        if (prepared.AccessKind != expectedKind) throw new SubagentControlRejectedException("subagent_control_binding_invalid");
        intent.DemandActive();
        switch (proposal.ToolId)
        {
            case "agent_spawn":
                var request = intent.Arguments.Deserialize<SubagentSpawnRequest>()!;
                var child = await children.CreateAsync(root.RunId, intent.InvocationId, request, ct).ConfigureAwait(false);
                return Receipt("subagent_spawned", new { agent_id = child.AgentId, role = request.Role, accepted = true });
            case "agent_send":
                var agent = intent.Arguments.GetProperty("agent_id").GetString()!;
                await agents.AppendMessageAsync(root.RunId, agent, intent.InvocationId,
                    intent.Arguments.GetProperty("message").GetString()!, ct).ConfigureAwait(false);
                return Receipt("subagent_message_queued", new { agent_id = agent, accepted = true });
            case "agent_cancel":
                var canceled = intent.Arguments.GetProperty("agent_id").GetString()!;
                await cancellation.CancelAsync(root.RunId, canceled, ct).ConfigureAwait(false);
                return Receipt("subagent_canceled", new { agent_id = canceled, canceled = true });
            case "agent_wait":
                return ToolExecutionReceipt.DeferredSubagentWait(prepared.ExecutionId, prepared.ToolCallId);
            default:
                throw new SubagentControlRejectedException("invalid_subagent_control");
        }

        ToolExecutionReceipt Receipt(string code, object output) => new(prepared.ExecutionId, prepared.ToolCallId,
            ToolExecutionOutcome.Succeeded, code, null, true, JsonSerializer.SerializeToUtf8Bytes(output),
            HarnessPayloadEncoding.Json, proposal.ProviderCallId);
    }
}

internal sealed class SubagentControlRejectedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
