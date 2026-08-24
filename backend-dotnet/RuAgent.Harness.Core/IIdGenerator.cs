namespace RuAgent.Harness.Core;

public interface IIdGenerator
{
    RunId NewRunId();
    ModelStepId NewModelStepId();
    ToolCallId NewToolCallId();
    ExecutionId NewExecutionId();
    ApprovalId NewApprovalId();
    TrustRequestId NewTrustRequestId();
    PolicyRuleId NewPolicyRuleId();
    EventId NewEventId();
    PayloadId NewPayloadId();
}
