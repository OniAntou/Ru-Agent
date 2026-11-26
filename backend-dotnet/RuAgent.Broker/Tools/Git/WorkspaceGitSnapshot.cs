namespace RuAgent.Broker.Tools.Git;

/// <summary>
/// Bounded, read-only repository facts the Host may place in prompt context. This is
/// context, not evidence: every value is either absent or a deliberately short sample,
/// and nothing here grants execution authority or replaces a tool receipt.
/// </summary>
public sealed record WorkspaceGitSnapshot(
    bool IsRepository,
    string? Root,
    string? Branch,
    string? HeadCommit,
    string? HeadSubject,
    int Modified,
    int Staged,
    int Untracked,
    int Conflicted,
    int? Ahead,
    int? Behind,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<string> RecentCommits,
    bool Truncated,
    bool StatusUnavailable)
{
    /// <summary>The workspace is not inside a Git repository (or Git is unavailable).</summary>
    public static WorkspaceGitSnapshot NotRepository { get; } = new(
        false, null, null, null, null, 0, 0, 0, 0, null, null,
        Array.Empty<string>(), Array.Empty<string>(), false, false);

    /// <summary>Whether the status sample is smaller than the actual working tree.</summary>
    public bool HasChangedPaths => ChangedPaths.Count > 0;

    /// <summary>Whether a clean working tree can be claimed. Truncated or failed status cannot.</summary>
    public bool DescribesWorkingTree => !Truncated && !StatusUnavailable;
}
