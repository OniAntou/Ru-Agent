using RuAgent.Harness.Core.Persistence;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Core.Subagents;

/// <summary>User action provenance; correlation identifiers do not create a model turn.</summary>
public static class SubagentUserApplyCommit
{
    public static HarnessCommit Create(AgentRun root, ToolProposal action, ExecutionId execution,
        bool uncertain, DateTimeOffset now, SubagentApplyStatus? outcome = null)
    {
        if (root.State != HarnessRunState.Running || action.RunId != root.RunId || action.ToolId != "agent_apply"
            || !action.ProviderCallId.StartsWith("user_apply_", StringComparison.Ordinal)
            || !SubagentContractValidator.Validate("agent_apply", action.Arguments).IsValid)
            throw new InvalidOperationException("subagent_user_apply_invalid");
        var payload = new PayloadId("payload_" + Guid.NewGuid().ToString("N"));
        var text = System.Text.Encoding.UTF8.GetBytes($"User requested integration of subagent {action.Arguments.GetProperty("agent_id").GetString()} "
            + $"result {action.Arguments.GetProperty("result_version").GetInt64()}. Broker outcome: {outcome?.ToString().ToLowerInvariant() ?? "unknown"}. "
            + (uncertain ? "Explicit user reconciliation is required before continuing." : "Inspect current workspace content before further edits."));
        return new(root.RunId, root.Version,
            root.Advance(uncertain ? new(HarnessRunState.ReconciliationRequired) : root.Status,
                root.BudgetWindow, root.ModelStepsInWindow, root.ToolCallsInWindow, 1,
                now < root.UpdatedAt ? root.UpdatedAt : now),
            [new(new("evt_" + Guid.NewGuid().ToString("N")), uncertain ? HarnessEventKind.ReconciliationRequired : HarnessEventKind.RunNoteAdded,
                modelStepId: action.ModelStepId, toolCallId: action.ToolCallId, executionId: execution,
                payloadId: payload, safeCode: uncertain ? "subagent_apply_user_unknown" : "subagent_apply_user_completed")],
            [new(payload, HarnessPayloadKind.ModelContextNote, HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Utf8Text, text.Length, text)], []);
    }
}
