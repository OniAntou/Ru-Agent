using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Core.Subagents;

/// <summary>Trusted orchestration metadata, encrypted at rest and never accepted from a tool argument.</summary>
public sealed record SubagentRuntimeScope(string ParentRoot, string WorkspaceRoot,
    SubagentParentPolicyIdentity ParentPolicy, string PolicySnapshotJson, string? WorktreeManifestJson,
    string ParentPolicySnapshotJson);
