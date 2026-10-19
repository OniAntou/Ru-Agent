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
    RequestInput,
    ResolveInput,
    ExhaustBudget,
    Continue,
    RequireReconciliation,
    ApplyReconciliation,
    BeginFinalHistory,
    AcknowledgeFinalHistory,
    Cancel,
    Fail,
    Interrupt,
    WaitForSubagents,
    SubagentsReady,
    CompleteSubagent,
    AdmitSubagent,
}
