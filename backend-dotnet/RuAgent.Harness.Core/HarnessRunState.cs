namespace RuAgent.Harness.Core;

public enum HarnessRunState
{
    Created = 0,
    WaitingWorkspaceTrust = 1,
    WaitingHistory = 2,
    Running = 3,
    WaitingApproval = 4,
    WaitingUser = 5,
    ReconciliationRequired = 6,
    Completed = 7,
    Failed = 8,
    Cancelled = 9,
    Interrupted = 10,
}

public static class HarnessRunStateExtensions
{
    public static bool IsTerminal(this HarnessRunState state) =>
        state is HarnessRunState.Completed
            or HarnessRunState.Failed
            or HarnessRunState.Cancelled
            or HarnessRunState.Interrupted;
}
