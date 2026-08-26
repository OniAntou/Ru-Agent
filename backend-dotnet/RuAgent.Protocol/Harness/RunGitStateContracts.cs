namespace RuAgent.Protocol.Harness;

public enum RunGitEntryKind
{
    Tracked,
    Untracked,
    Unmerged,
}

public sealed record RunGitBranchHeader(
    string? HeadOid,
    string HeadRef,
    bool IsDetached,
    string? Upstream = null,
    long? AheadCount = null,
    long? BehindCount = null);

public sealed record RunGitStatusEntry(
    string IndexCode,
    string WorktreeCode,
    RunGitEntryKind Kind,
    bool IsSubmodule,
    string Path,
    string? OriginalPath,
    int? RenameScore)
{
    public bool HasStagedChanges => Kind != RunGitEntryKind.Untracked && IndexCode != ".";
    public bool HasUnstagedChanges => Kind != RunGitEntryKind.Untracked && WorktreeCode != ".";
}

public sealed record RunGitStatusParseResult(
    RunGitBranchHeader Branch,
    IReadOnlyList<RunGitStatusEntry> Entries,
    bool Truncated,
    IReadOnlyList<string> Errors);

/// <summary>
/// Bounded, content-free Git/worktree state for one run boundary. Only hashes, codes,
/// and paths are carried; raw diffs and file contents never enter this contract.
/// </summary>
public sealed record RunGitWorktreeSnapshot(
    string RepositoryRoot,
    string? HeadOid,
    string BranchName,
    bool IsDetached,
    string IndexHash,
    bool HasStagedChanges,
    bool ManagedCommitEligible,
    IReadOnlyList<RunGitStatusEntry> Entries,
    IReadOnlyDictionary<string, string> ObservedFileHashes,
    bool StatusTruncated,
    bool FileHashesTruncated,
    DateTimeOffset CapturedAtUtc);

/// <summary>
/// One verified before/after hash pair for a path, proven by a file-tool mutation or a
/// bracketed terminal execution window. Evidence with mismatched hashes attributes nothing.
/// </summary>
public sealed record RunGitMutationEvidence(string Path, string BeforeHash, string AfterHash);

public sealed record RunChangeAttributionResult(
    IReadOnlyList<string> AttributedPaths,
    IReadOnlyList<string> ExcludedPreExistingPaths,
    IReadOnlyList<string> AmbiguousPaths);
