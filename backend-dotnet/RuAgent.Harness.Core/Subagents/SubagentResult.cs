namespace RuAgent.Harness.Core.Subagents;

public sealed record SubagentResult(string AgentId, long Version, RunId RunId, HarnessRunState State,
    string? Code, PayloadId PayloadId, bool Delivered);

public sealed record SubagentPublicationCandidate(string AgentId, RunId RootRunId, RunId RunId,
    RuAgent.Protocol.Subagents.SubagentRole Role, HarnessRunState State);
