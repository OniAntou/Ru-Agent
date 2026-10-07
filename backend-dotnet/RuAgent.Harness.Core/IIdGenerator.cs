namespace RuAgent.Harness.Core;

public interface IIdGenerator
{
    RunId NewRunId();
    ModelStepId NewModelStepId();
    ToolCallId NewToolCallId();
    ExecutionId NewExecutionId();
    ApprovalId NewApprovalId();
    InputId NewInputId();
    TrustRequestId NewTrustRequestId();
    PolicyRuleId NewPolicyRuleId();
    EventId NewEventId();
    PayloadId NewPayloadId();
}
