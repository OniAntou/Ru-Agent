namespace RuAgent.Broker.Subagents.Workspaces;

public sealed class SubagentWorkspaceException(string code, Exception? inner = null) : IOException(code, inner)
{
    public string Code { get; } = code;
}

public sealed record SubagentSnapshotFile(string RelativePath, string Hash, int Mode, long Length);
public sealed record SubagentSnapshot(string SnapshotId, string ParentRunId, string ParentRoot,
    string Head, string GitDirectory, string CommonDirectory, string ParentDirectoryIdentity,
    string BaseDirectory, IReadOnlyList<SubagentSnapshotFile> Files, string? OwnedDirectoryIdentity = null,
    string? GitDirectoryIdentity = null, string? CommonDirectoryIdentity = null);
public sealed record SubagentWorkspaceManifest(string AgentId, SubagentSnapshot Snapshot,
    string WorkspaceRoot, string DirectoryIdentity, string GitDirectory, string CommonDirectory,
    string? GitDirectoryIdentity = null);
