namespace RuAgent.Broker.Tools.Git;

public enum ManagedCommitPhase
{
    Committed,
    Aborted,
    Failed,
    ReconciliationRequired,
}

/// <summary>
/// The exact, hash-bound preview of one managed commit: the run-attributed paths that
/// would be staged on top of HEAD, the SHA-256 of the resulting cached patch, and the
/// observed worktree hashes used to prove staleness at execution time.
/// </summary>
public sealed record ManagedCommitPlan(
    string RepositoryRoot,
    string HeadOid,
    string BranchName,
    IReadOnlyList<string> StagedPaths,
    string DiffHash,
    IReadOnlyDictionary<string, string> ObservedPathHashes,
    DateTimeOffset CreatedAtUtc);

public sealed record ManagedCommitPlanResult(
    bool Succeeded,
    string? RefusalCode,
    ManagedCommitPlan? Plan)
{
    public static ManagedCommitPlanResult Refused(string code) => new(false, code, null);

    public static ManagedCommitPlanResult Approved(ManagedCommitPlan plan) => new(true, null, plan);
}

public sealed record ManagedCommitOutcome(
    ManagedCommitPhase Phase,
    string Code,
    string? NewHeadOid = null,
    IReadOnlyList<string>? ReconciliationHints = null)
{
    public static ManagedCommitOutcome Aborted(string code) => new(ManagedCommitPhase.Aborted, code);

    public static ManagedCommitOutcome Reconcile(string code, string? newHeadOid, params string[] hints) =>
        new(ManagedCommitPhase.ReconciliationRequired, code, newHeadOid, hints);
}
