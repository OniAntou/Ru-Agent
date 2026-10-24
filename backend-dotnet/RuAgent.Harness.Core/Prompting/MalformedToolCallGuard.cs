using RuAgent.Harness.Core.Context;
using RuAgent.Protocol;

namespace RuAgent.Harness.Core.Prompting;

internal static class MalformedToolCallGuard
{
    // A changing file name must not buy an unlimited retry budget for an
    // identical invalid enum/type. A genuinely repaired schema is allowed.
    public static bool TryDescribe(ModelContextSnapshot context, IReadOnlyList<ModelToolCall> proposed,
        out string? code)
    {
        code = null;
        if (proposed.Count == 0) return false;
        foreach (var call in proposed)
        {
            var issue = ToolArgumentDiagnostics.Find(call.ToolId, call.ToolVersion, call.Arguments);
            if (issue is null) continue;
            var failures = 0;
            var proposals = new Dictionary<string, ModelToolCall>(StringComparer.Ordinal);
            foreach (var entry in context.Entries.TakeLast(96))
            {
                if (entry.Role == ModelContextRole.User) { failures = 0; proposals.Clear(); }
                if (entry.AssistantStep is { } step)
                {
                    proposals.Clear();
                    foreach (var prior in step.ToolCalls) proposals[prior.ProviderCallId] = prior;
                }
                if (entry.ToolReceipt is not { ProviderCallId: { } id } receipt
                    || !proposals.TryGetValue(id, out var priorCall) || priorCall.ToolId != call.ToolId) continue;
                if (receipt.Outcome == ToolExecutionOutcome.Succeeded) { failures = 0; continue; }
                if (receipt.Code == "malformed_call" && !receipt.SideEffectsPossible
                    && ToolArgumentDiagnostics.Find(priorCall.ToolId, priorCall.ToolVersion, priorCall.Arguments)?.Signature == issue.Signature)
                    failures++;
            }
            if (failures >= 3) { code = "malformed_call"; return true; }
        }
        return false;
    }
}
