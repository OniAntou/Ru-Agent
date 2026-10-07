namespace RuAgent.Harness.Core;

using System.Security.Cryptography;

public sealed class CryptographicIdGenerator : IIdGenerator
{
    public RunId NewRunId() => new(CreateValue("run_"));
    public ModelStepId NewModelStepId() => new(CreateValue("step_"));
    public ToolCallId NewToolCallId() => new(CreateValue("call_"));
    public ExecutionId NewExecutionId() => new(CreateValue("exec_"));
    public ApprovalId NewApprovalId() => new(CreateValue("apr_"));
    public InputId NewInputId() => new(CreateValue("inp_"));
    public TrustRequestId NewTrustRequestId() => new(CreateValue("trust_"));
    public PolicyRuleId NewPolicyRuleId() => new(CreateValue("rule_"));
    public EventId NewEventId() => new(CreateValue("evt_"));
    public PayloadId NewPayloadId() => new(CreateValue("payload_"));

    private static string CreateValue(string prefix) =>
        prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
