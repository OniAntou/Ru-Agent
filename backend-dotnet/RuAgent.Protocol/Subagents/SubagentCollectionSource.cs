namespace RuAgent.Protocol.Subagents;

/// <summary>Trusted encrypted artifact metadata and configured storage root; never model arguments or execution authority.</summary>
public sealed record SubagentCollectionSource(string RootRunId, string ChildRunId, string InternalSessionId,
    string AgentId, string ParentRoot, string WorkspaceRoot, string StateDirectory, string WorktreeManifestJson);
