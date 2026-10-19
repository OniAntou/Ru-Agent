namespace RuAgent.Protocol.Subagents;

/// <summary>The original authority ceiling retained with a durable child attempt.</summary>
public sealed record SubagentParentPolicyIdentity(long Version, string Sha256);
