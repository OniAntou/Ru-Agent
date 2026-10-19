using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Core.Subagents;

public static class SubagentAdmission
{
    /// <summary>Persistence commits this transition with the frozen attempt deadline.</summary>
    public static HarnessCommit CreateCommit(AgentRun run, EventId eventId, DateTimeOffset now)
    {
        var state = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.AdmitSubagent);
        if (!state.Accepted) throw new InvalidOperationException(state.ErrorCode);
        return new HarnessCommit(run.RunId, run.Version,
            run.Advance(state.Next, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1, now),
            [new(eventId, HarnessEventKind.RunStateChanged, safeCode: "subagent_admitted")], [], []);
    }

    public static HarnessCommit CreateTerminalCommit(AgentRun run, EventId eventId, bool canceled, string code, DateTimeOffset now)
    {
        var state = AgentRunStateMachine.TryTransition(run.Status, canceled ? HarnessRunTransition.Cancel : HarnessRunTransition.Fail);
        if (!state.Accepted) throw new InvalidOperationException(state.ErrorCode);
        return new(run.RunId, run.Version,
            run.Advance(state.Next, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1,
                now < run.UpdatedAt ? run.UpdatedAt : now, code),
            [new(eventId, canceled ? HarnessEventKind.RunStateChanged : HarnessEventKind.RunFailed, safeCode: code)], [], []);
    }
}
