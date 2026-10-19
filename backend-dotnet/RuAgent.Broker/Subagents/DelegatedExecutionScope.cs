using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Subagents;

/// <summary>Host-bound authority, never deserialized from model/tool payloads.</summary>
public sealed record DelegatedExecutionScope(string ParentRunId, string ChildRunId,
    string ParentSessionId, string ChildSessionId, string ParentRoot, string ChildRoot,
    long ParentSnapshotVersion, string ParentSnapshotHash, SubagentRole Role,
    RuAgent.Broker.Policy.BoundPolicySnapshot? ParentCeiling = null)
{
    internal RuAgent.Broker.Runtime.BrokerRunLifetime? ParentLifetime { get; init; }
    internal RuAgent.Broker.Runtime.BrokerRunLifetime? ChildLifetime { get; init; }
}
