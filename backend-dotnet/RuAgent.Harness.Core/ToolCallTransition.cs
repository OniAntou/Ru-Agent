namespace RuAgent.Harness.Core;

public enum ToolCallTransition
{
    Allow,
    RequestApproval,
    Deny,
    ConsumeApproval,
    ExpireApproval,
    Dispatch,
    Succeed,
    Fail,
    TimeOut,
    Cancel,
    MarkOutcomeUnknown,
}
