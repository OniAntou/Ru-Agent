namespace RuAgent.Harness.Core;

public enum HarnessRunTransition
{
    BeginWorkspaceTrust,
    ApproveWorkspaceTrust,
    DenyWorkspaceTrust,
    BeginUserHistory,
    AcknowledgeUserHistory,
    RequestApproval,
    ResolveApproval,
    ExhaustBudget,
    Continue,
    RequireReconciliation,
    ApplyReconciliation,
    BeginFinalHistory,
    AcknowledgeFinalHistory,
    Cancel,
    Fail,
    Interrupt,
}
