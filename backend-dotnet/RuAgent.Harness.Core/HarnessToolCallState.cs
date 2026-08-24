namespace RuAgent.Harness.Core;

public enum HarnessToolCallState
{
    Proposed,
    Allowed,
    AwaitingApproval,
    Denied,
    Executing,
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
    OutcomeUnknown,
}

public static class HarnessToolCallStateExtensions
{
    public static bool IsTerminal(this HarnessToolCallState state) => state is
        HarnessToolCallState.Denied
        or HarnessToolCallState.Succeeded
        or HarnessToolCallState.Failed
        or HarnessToolCallState.TimedOut
        or HarnessToolCallState.Cancelled
        or HarnessToolCallState.OutcomeUnknown;
}
