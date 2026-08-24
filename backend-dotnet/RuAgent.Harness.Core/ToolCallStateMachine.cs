namespace RuAgent.Harness.Core;

public static class ToolCallStateMachine
{
    public static TransitionResult<HarnessToolCallState> TryTransition(
        HarnessToolCallState current,
        ToolCallTransition transition)
    {
        if (current.IsTerminal())
        {
            return TransitionResult<HarnessToolCallState>.Reject(current, "tool_call_terminal");
        }

        HarnessToolCallState? next = (current, transition) switch
        {
            (HarnessToolCallState.Proposed, ToolCallTransition.Allow) =>
                HarnessToolCallState.Allowed,
            (HarnessToolCallState.Proposed, ToolCallTransition.RequestApproval) =>
                HarnessToolCallState.AwaitingApproval,
            (HarnessToolCallState.Proposed, ToolCallTransition.Deny) =>
                HarnessToolCallState.Denied,
            (HarnessToolCallState.AwaitingApproval, ToolCallTransition.ConsumeApproval) =>
                HarnessToolCallState.Allowed,
            (HarnessToolCallState.AwaitingApproval, ToolCallTransition.Deny) =>
                HarnessToolCallState.Denied,
            (HarnessToolCallState.AwaitingApproval, ToolCallTransition.ExpireApproval) =>
                HarnessToolCallState.Proposed,
            (HarnessToolCallState.Allowed, ToolCallTransition.Dispatch) =>
                HarnessToolCallState.Executing,
            (HarnessToolCallState.Executing, ToolCallTransition.Succeed) =>
                HarnessToolCallState.Succeeded,
            (HarnessToolCallState.Executing, ToolCallTransition.Fail) =>
                HarnessToolCallState.Failed,
            (HarnessToolCallState.Executing, ToolCallTransition.TimeOut) =>
                HarnessToolCallState.TimedOut,
            (HarnessToolCallState.Executing, ToolCallTransition.Cancel) =>
                HarnessToolCallState.Cancelled,
            (HarnessToolCallState.Executing, ToolCallTransition.MarkOutcomeUnknown) =>
                HarnessToolCallState.OutcomeUnknown,
            _ => null,
        };

        return next is { } accepted
            ? TransitionResult<HarnessToolCallState>.Accept(accepted)
            : TransitionResult<HarnessToolCallState>.Reject(
                current,
                "invalid_tool_call_transition");
    }
}
