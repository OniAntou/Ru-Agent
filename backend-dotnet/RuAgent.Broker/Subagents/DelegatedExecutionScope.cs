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
    /// <summary>
    /// Detached (ADR 0045): the child outlives a normal close of its parent's lease and is then
    /// held to the parent snapshot retained while that lease was live. Explicit child cancel,
    /// parent-session close and runtime disposal still revoke it.
    /// </summary>
    internal bool Detached { get; init; }
}
