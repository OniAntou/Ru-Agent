using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Core.Subagents;

public static class SubagentInboxDelivery
{
    public static HarnessCommit CreateCommit(AgentRun run, ReadOnlyMemory<byte> content, int count, DateTimeOffset now)
    {
        if (run.State != HarnessRunState.Running || count < 1 || count > 64)
            throw new ArgumentException("subagent_inbox_boundary_invalid");
        var payload = new PayloadId("payload_" + Guid.NewGuid().ToString("N"));
        var next = run.Advance(run.Status, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1,
            now < run.UpdatedAt ? run.UpdatedAt : now);
        return new(run.RunId, run.Version, next,
            [new(new("evt_" + Guid.NewGuid().ToString("N")), HarnessEventKind.RunNoteAdded,
                payloadId: payload, safeCode: "subagent_messages_received", safeCount: count)],
            [new(payload, HarnessPayloadKind.UserPrompt,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Utf8Text, content.Length, content)], []);
    }
}
