using System.Text.Json;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Core.Subagents;

/// <summary>Written with the parked projection, referencing the already authorized original tool call.</summary>
public sealed record SubagentWaitIntent
{
    public SubagentWaitIntent(ModelStepId stepId, ToolCallId callId, ExecutionId executionId, string providerCallId, JsonElement arguments)
    {
        _ = stepId.Value;
        _ = callId.Value;
        _ = executionId.Value;
        if (!ProviderCallIdContract.IsValid(providerCallId) || !SubagentContractValidator.Validate("agent_wait", arguments).IsValid)
            throw new ArgumentException("subagent_wait_invalid");
        ModelStepId = stepId;
        ToolCallId = callId;
        ExecutionId = executionId;
        ProviderCallId = providerCallId;
        AgentIds = Array.AsReadOnly(arguments.GetProperty("agent_ids").EnumerateArray().Select(value => value.GetString()!).ToArray());
        TimeoutMs = arguments.TryGetProperty("timeout_ms", out var timeout) ? timeout.GetInt32() : 30000;
    }

    public ModelStepId ModelStepId { get; }
    public ToolCallId ToolCallId { get; }
    public ExecutionId ExecutionId { get; }
    public string ProviderCallId { get; }
    public IReadOnlyList<string> AgentIds { get; }
    public int TimeoutMs { get; }
}
