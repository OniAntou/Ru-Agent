namespace RuAgent.Harness.Core;

public enum HarnessRunState
{
    Created,
    WaitingWorkspaceTrust,
    WaitingHistory,
    Running,
    WaitingApproval,
    WaitingUser,
    ReconciliationRequired,
    Completed,
    Failed,
    Cancelled,
    Interrupted,
}

public static class HarnessRunStateExtensions
{
    public static bool IsTerminal(this HarnessRunState state) =>
        state is HarnessRunState.Completed
            or HarnessRunState.Failed
            or HarnessRunState.Cancelled
            or HarnessRunState.Interrupted;
}
