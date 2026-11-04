namespace RuAgent.Broker.Subagents.Workspaces;

public sealed class SubagentWorkspaceException(string code, Exception? inner = null) : IOException(code, inner)
{
    public string Code { get; } = code;
    // Set only by snapshot capture before it starts creating any directory.
    public bool NoWorkspaceAllocated { get; internal set; }
}

public sealed record SubagentSnapshotFile(string RelativePath, string Hash, int Mode, long Length);
/// <remarks>
/// <see cref="PrivateRepository"/> is set when the workspace is not a usable Git source
/// (ADR 0042): Head and the Git directories then name the Ru-owned private base repository
/// inside the snapshot directory. It is omitted from JSON when null so Git-sourced
/// evidence keeps the exact shape earlier versions persisted.
/// </remarks>
public sealed record SubagentSnapshot(string SnapshotId, string ParentRunId, string ParentRoot,
    string Head, string GitDirectory, string CommonDirectory, string ParentDirectoryIdentity,
    string BaseDirectory, IReadOnlyList<SubagentSnapshotFile> Files, string? OwnedDirectoryIdentity = null,
    string? GitDirectoryIdentity = null, string? CommonDirectoryIdentity = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? PrivateRepository = null)
{
    /// <summary>The repository the child worktree is registered in.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string RepositoryRoot => PrivateRepository ?? ParentRoot;
}
public sealed record SubagentWorkspaceManifest(string AgentId, SubagentSnapshot Snapshot,
    string WorkspaceRoot, string DirectoryIdentity, string GitDirectory, string CommonDirectory,
    string? GitDirectoryIdentity = null);
