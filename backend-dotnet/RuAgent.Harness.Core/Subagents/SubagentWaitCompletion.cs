using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Core.Subagents;

public static class SubagentWaitCompletion
{
    public static HarnessCommit CreateCommit(AgentRun run, SubagentWaitIntent wait, ReadOnlyMemory<byte> output,
        bool timedOut, PayloadId payloadId, EventId completedId, EventId stateId, DateTimeOffset now)
    {
        var transition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.SubagentsReady);
        if (!transition.Accepted) throw new InvalidOperationException(transition.ErrorCode);
        var next = run.Advance(transition.Next, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 2,
            now < run.UpdatedAt ? run.UpdatedAt : now);
        return new(run.RunId, run.Version, next,
            [new(completedId, HarnessEventKind.ToolExecutionCompleted, modelStepId: wait.ModelStepId,
                toolCallId: wait.ToolCallId, executionId: wait.ExecutionId, providerCallId: wait.ProviderCallId,
                payloadId: payloadId, safeCode: timedOut ? "subagent_wait_timeout" : "subagent_wait_completed",
                toolOutcome: ToolExecutionOutcome.Succeeded, sideEffectsPossible: false),
             new(stateId, HarnessEventKind.RunStateChanged, safeCode: "running")],
            [new(payloadId, HarnessPayloadKind.ToolOutput, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Json, output.Length, output)], []);
    }
}
