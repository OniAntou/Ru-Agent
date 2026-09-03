using System.Security.Cryptography;
using System.Text;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools.Git;

/// <summary>
/// The managed local-commit workflow for `terminal.run git commit`. Previews are built
/// on a temporary GIT_INDEX_FILE index and never touch the real index; approved
/// executions revalidate HEAD, branch, the empty-real-index requirement, observed
/// worktree hashes, and the preview diff hash before staging exactly the approved paths.
/// A failed commit restores the exact pre-operation index bytes only while the index is
/// still in the service-owned state; any concurrent or hook-driven mutation routes to
/// explicit reconciliation. No reset, clean, force checkout, whole-tree stage, push,
/// or pull exists anywhere in this workflow.
/// </summary>
public sealed class ManagedCommitService(IGitCli cli)
{
    public const int MaxCommitMessageChars = 2000;
    public const int MaxStagedPaths = 500;
    private const int MaxIndexBytes = 16 * 1024 * 1024;
    private const int MaxPathChars = 512;
    private const string MissingHash = "missing";

    public static bool IsManagedCommitInvocation(string executable, IReadOnlyList<string> arguments)
    {
        if (!IsGitExecutable(executable))
        {
            return false;
        }

        var skippingValue = false;
        foreach (var argument in arguments)
        {
            if (skippingValue)
            {
                skippingValue = false;
                continue;
            }

            if (argument.Length == 0)
            {
                return false;
            }

            if (argument.StartsWith('-'))
            {
                if (argument is "-c" or "-C")
                {
                    skippingValue = true;
                }

                continue;
            }

            return string.Equals(argument, "commit", StringComparison.Ordinal);
        }

        return false;
    }

    public static string? TryExtractCommitMessage(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        for (var i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (arg is "-m" or "--message")
            {
                if (i + 1 < arguments.Count)
                {
                    return arguments[i + 1];
                }

                return null;
            }

            if (arg.StartsWith("-m", StringComparison.Ordinal) && arg.Length > 2)
            {
                return arg[2..];
            }

            if (arg.StartsWith("--message=", StringComparison.Ordinal))
            {
                return arg["--message=".Length..];
            }
        }

        return null;
    }

    public async Task<ManagedCommitPlanResult> BuildPreviewAsync(
        string repositoryRoot,
        IReadOnlyList<string> attributedPaths,
        IReadOnlyCollection<string> dirtyAtStartPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attributedPaths);
        ArgumentNullException.ThrowIfNull(dirtyAtStartPaths);

        var effectivePaths = EffectivePaths(attributedPaths, dirtyAtStartPaths);
        if (effectivePaths.Count == 0)
        {
            return ManagedCommitPlanResult.Refused("nothing_attributed");
        }

        var headOid = await ReadHeadOidAsync(repositoryRoot, cancellationToken);
        if (headOid is null)
        {
            return ManagedCommitPlanResult.Refused("head_unborn");
        }

        var branchName = await ReadBranchNameAsync(repositoryRoot, cancellationToken);
        if (branchName is null)
        {
            return ManagedCommitPlanResult.Refused("no_branch");
        }

        var realIndexEmpty = await RealIndexIsEmptyAsync(repositoryRoot, cancellationToken);
        if (realIndexEmpty is null)
        {
            return ManagedCommitPlanResult.Refused("index_unknown");
        }

        if (realIndexEmpty == false)
        {
            return ManagedCommitPlanResult.Refused("index_not_empty");
        }

        using var temporary = TemporaryGitIndex.Create();
        var stagedDiffHash = await BuildTemporaryStagedDiffHashAsync(
            repositoryRoot, effectivePaths, temporary, cancellationToken);
        if (stagedDiffHash.DiffHash is null)
        {
            return ManagedCommitPlanResult.Refused(stagedDiffHash.RefusalCode ?? "preview_failed");
        }

        var observedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in stagedDiffHash.StagedPaths!)
        {
            var hash = await HashWorktreeFileOrNullAsync(repositoryRoot, path, cancellationToken);
            if (hash is null)
            {
                return ManagedCommitPlanResult.Refused("worktree_unreadable");
            }

            observedHashes[path] = hash;
        }

        return ManagedCommitPlanResult.Approved(new ManagedCommitPlan(
            RepositoryRoot: repositoryRoot,
            HeadOid: headOid,
            BranchName: branchName,
            StagedPaths: stagedDiffHash.StagedPaths,
            DiffHash: stagedDiffHash.DiffHash,
            ObservedPathHashes: observedHashes,
            CreatedAtUtc: DateTimeOffset.UtcNow));
    }

    public async Task<ManagedCommitOutcome> ExecuteApprovedAsync(
        ManagedCommitPlan plan,
        string message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!IsValidCommitMessage(message))
        {
            return ManagedCommitOutcome.Aborted("invalid_message");
        }

        var root = plan.RepositoryRoot;

        var headOid = await ReadHeadOidAsync(root, cancellationToken);
        if (headOid is null || !string.Equals(headOid, plan.HeadOid, StringComparison.Ordinal))
        {
            return ManagedCommitOutcome.Aborted("head_moved");
        }

        var branchName = await ReadBranchNameAsync(root, cancellationToken);
        if (branchName is null || !string.Equals(branchName, plan.BranchName, StringComparison.Ordinal))
        {
            return ManagedCommitOutcome.Aborted("branch_moved");
        }

        var realIndexEmpty = await RealIndexIsEmptyAsync(root, cancellationToken);
        if (realIndexEmpty is null)
        {
            return ManagedCommitOutcome.Aborted("index_unknown");
        }

        if (realIndexEmpty == false)
        {
            return ManagedCommitOutcome.Aborted("index_not_empty");
        }

        if (!await WorktreeMatchesPreviewAsync(plan, cancellationToken))
        {
            return ManagedCommitOutcome.Aborted("worktree_changed");
        }

        using var temporary = TemporaryGitIndex.Create();
        var rebuilt = await BuildTemporaryStagedDiffHashAsync(
            root, plan.StagedPaths, temporary, cancellationToken);
        if (!string.Equals(rebuilt.DiffHash, plan.DiffHash, StringComparison.Ordinal))
        {
            return ManagedCommitOutcome.Aborted(rebuilt.DiffHash is null ? rebuilt.RefusalCode ?? "preview_stale" : "preview_stale");
        }

        var unrelatedBefore = await CaptureUnrelatedStateAsync(root, plan.StagedPaths, cancellationToken);
        if (unrelatedBefore is null)
        {
            return ManagedCommitOutcome.Reconcile("status_unavailable", null);
        }

        var gitDirResult = await cli.RunAsync(
            new[] { "rev-parse", "--absolute-git-dir" }, root, cancellationToken);
        if (!gitDirResult.Succeeded)
        {
            return ManagedCommitOutcome.Reconcile("git_dir_unknown", null);
        }

        var gitDir = gitDirResult.Stdout.Trim();
        var indexPath = Path.Combine(gitDir.Replace('/', Path.DirectorySeparatorChar), "index");
        var preOperation = ReadIndexState(indexPath);

        var stage = await cli.RunAsync(
            BuildAddArguments(plan.StagedPaths), root, cancellationToken);
        if (!stage.Succeeded)
        {
            return await RestoreIfServiceOwnedAsync(
                root, plan.DiffHash, indexPath, preOperation, ManagedCommitOutcome.Aborted("stage_failed"), cancellationToken);
        }

        var stagedDiff = await cli.RunAsync(new[] { "diff", "--cached" }, root, cancellationToken);
        if (!stagedDiff.Succeeded
            || !string.Equals(HashText(stagedDiff.Stdout), plan.DiffHash, StringComparison.Ordinal))
        {
            return await RestoreIfServiceOwnedAsync(
                root,
                plan.DiffHash,
                indexPath,
                preOperation,
                ManagedCommitOutcome.Aborted("staged_diff_mismatch"),
                cancellationToken);
        }

        var commit = await cli.RunAsync(
            new[] { "-c", "commit.gpgsign=false", "commit", "-m", message },
            root,
            cancellationToken,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["GIT_EDITOR"] = ":" });
        if (!commit.Succeeded)
        {
            return await RestoreIfServiceOwnedAsync(
                root,
                plan.DiffHash,
                indexPath,
                preOperation,
                new ManagedCommitOutcome(ManagedCommitPhase.Failed, "commit_failed"),
                cancellationToken);
        }

        var newHeadOid = await ReadHeadOidAsync(root, cancellationToken);
        if (newHeadOid is null || string.Equals(newHeadOid, headOid, StringComparison.Ordinal))
        {
            return ManagedCommitOutcome.Reconcile("commit_outcome_unknown", newHeadOid);
        }

        var parentResult = await cli.RunAsync(new[] { "rev-parse", "HEAD^" }, root, cancellationToken);
        if (!parentResult.Succeeded
            || !string.Equals(parentResult.Stdout.Trim(), headOid, StringComparison.Ordinal))
        {
            return ManagedCommitOutcome.Reconcile("head_ambiguous", newHeadOid);
        }

        var committedDiff = await cli.RunAsync(
            new[] { "diff", "HEAD^", "HEAD" }, root, cancellationToken);
        if (!committedDiff.Succeeded
            || !string.Equals(HashText(committedDiff.Stdout), plan.DiffHash, StringComparison.Ordinal))
        {
            return ManagedCommitOutcome.Reconcile("post_commit_diff_mismatch", newHeadOid);
        }

        var unrelatedAfter = await CaptureUnrelatedStateAsync(root, plan.StagedPaths, cancellationToken);
        if (unrelatedAfter is null
            || !DictionariesEqual(unrelatedBefore, unrelatedAfter))
        {
            return ManagedCommitOutcome.Reconcile("worktree_changed_by_hook", newHeadOid);
        }

        return new ManagedCommitOutcome(ManagedCommitPhase.Committed, "committed", newHeadOid);
    }

    // ------------------------------------------------------------------
    // Preview building.
    // ------------------------------------------------------------------

    private async Task<(string? DiffHash, string? RefusalCode, IReadOnlyList<string>? StagedPaths)>
        BuildTemporaryStagedDiffHashAsync(
            string repositoryRoot,
            IReadOnlyList<string> effectivePaths,
            TemporaryGitIndex temporary,
            CancellationToken cancellationToken)
    {
        var readTree = await cli.RunAsync(
            new[] { "read-tree", "HEAD" },
            repositoryRoot,
            cancellationToken,
            temporary.Environment);
        if (!readTree.Succeeded)
        {
            return (null, "head_unreadable", null);
        }

        var add = await cli.RunAsync(
            BuildAddArguments(effectivePaths),
            repositoryRoot,
            cancellationToken,
            temporary.Environment);
        if (!add.Succeeded)
        {
            return (null, "path_invalid", null);
        }

        var cachedDiff = await cli.RunAsync(
            new[] { "diff", "--cached" },
            repositoryRoot,
            cancellationToken,
            temporary.Environment);
        if (cachedDiff.FailureReason == "git_output_truncated")
        {
            return (null, "diff_too_large", null);
        }

        if (!cachedDiff.Succeeded)
        {
            return (null, "preview_failed", null);
        }

        if (cachedDiff.Stdout.Length == 0)
        {
            return (null, "nothing_attributed", null);
        }

        return (HashText(cachedDiff.Stdout), null, effectivePaths);
    }

    private async Task<bool?> RealIndexIsEmptyAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var result = await cli.RunAsync(
            new[] { "diff", "--cached", "--quiet" }, repositoryRoot, cancellationToken);
        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => null,
        };
    }

    private async Task<string?> ReadHeadOidAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var result = await cli.RunAsync(new[] { "rev-parse", "HEAD" }, repositoryRoot, cancellationToken);
        return result.Succeeded && result.Stdout.Trim().Length == 40 ? result.Stdout.Trim() : null;
    }

    private async Task<string?> ReadBranchNameAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var result = await cli.RunAsync(
            new[] { "symbolic-ref", "--quiet", "--short", "HEAD" },
            repositoryRoot,
            cancellationToken);
        return result.Succeeded && result.Stdout.Trim().Length > 0 ? result.Stdout.Trim() : null;
    }

    private async Task<bool> WorktreeMatchesPreviewAsync(ManagedCommitPlan plan, CancellationToken cancellationToken)
    {
        foreach (var (path, expected) in plan.ObservedPathHashes)
        {
            var actual = await HashWorktreeFileOrNullAsync(plan.RepositoryRoot, path, cancellationToken);
            if (actual is null || !string.Equals(actual, expected, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<IReadOnlyDictionary<string, string>?> CaptureUnrelatedStateAsync(
        string repositoryRoot,
        IReadOnlyList<string> approvedPaths,
        CancellationToken cancellationToken)
    {
        var status = await cli.RunAsync(
            new[] { "status", "--porcelain=v2" }, repositoryRoot, cancellationToken);
        if (!status.Succeeded || status.FailureReason == "git_output_truncated")
        {
            return null;
        }

        var parsed = GitStatusParser.Parse(status.Stdout);
        if (parsed.Errors.Count > 0)
        {
            return null;
        }

        var approved = approvedPaths.ToHashSet(StringComparer.Ordinal);
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in parsed.Entries)
        {
            if (approved.Contains(entry.Path))
            {
                continue;
            }

            var hash = await HashWorktreeFileOrNullAsync(repositoryRoot, entry.Path, cancellationToken);
            if (hash is null)
            {
                return null;
            }

            state[entry.Path] = hash;
        }

        return state;
    }

    // ------------------------------------------------------------------
    // Restore / reconciliation routing.
    // ------------------------------------------------------------------

    /// <summary>
    /// The index is still service-owned only while its cached diff hash equals the
    /// approved preview hash. A stat-cache refresh keeps the same semantic state and is
    /// safe to restore over; any foreign staged content routes to reconciliation with
    /// the index left exactly as the foreign actor left it.
    /// </summary>
    private async Task<ManagedCommitOutcome> RestoreIfServiceOwnedAsync(
        string repositoryRoot,
        string expectedDiffHash,
        string indexPath,
        IndexSnapshot preOperationSnapshot,
        ManagedCommitOutcome outcomeIfRestored,
        CancellationToken cancellationToken)
    {
        var currentDiff = await cli.RunAsync(new[] { "diff", "--cached" }, repositoryRoot, cancellationToken);
        if (!currentDiff.Succeeded || !string.Equals(HashText(currentDiff.Stdout), expectedDiffHash, StringComparison.Ordinal))
        {
            return ManagedCommitOutcome.Reconcile("index_mutated_during_commit", null);
        }

        if (!TryRestoreIndex(indexPath, preOperationSnapshot))
        {
            return ManagedCommitOutcome.Reconcile("index_restore_failed", null);
        }

        return outcomeIfRestored;
    }

    private static IndexSnapshot ReadIndexState(string indexPath)
    {
        try
        {
            if (!File.Exists(indexPath))
            {
                return new IndexSnapshot(false, null);
            }

            return new IndexSnapshot(true, ReadBoundedBytes(indexPath, MaxIndexBytes));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new IndexSnapshot(true, null);
        }
    }

    private static bool TryRestoreIndex(string indexPath, IndexSnapshot? snapshot)
    {
        try
        {
            if (snapshot is null)
            {
                return false;
            }

            if (!snapshot.Exists)
            {
                if (File.Exists(indexPath))
                {
                    File.Delete(indexPath);
                }

                return true;
            }

            // An existing index that could not be captured is not safely
            // restorable. Leave it untouched and route the caller to reconciliation;
            // deleting it would destroy unrelated user state.
            if (snapshot.Bytes is null)
            {
                return false;
            }

            var directory = Path.GetDirectoryName(indexPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            var temporaryPath = Path.Combine(directory, $".ru-index-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
                {
                    stream.Write(snapshot.Bytes, 0, snapshot.Bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, indexPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }

            var restored = ReadBoundedBytes(indexPath, MaxIndexBytes);
            return DictionariesEqual(restored, snapshot.Bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record IndexSnapshot(bool Exists, byte[]? Bytes);

    // ------------------------------------------------------------------
    // Pure helpers.
    // ------------------------------------------------------------------

    private static IReadOnlyList<string> EffectivePaths(
        IReadOnlyList<string> attributedPaths,
        IReadOnlyCollection<string> dirtyAtStartPaths)
    {
        var excluded = dirtyAtStartPaths.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var effective = new List<string>();
        foreach (var rawPath in attributedPaths)
        {
            var path = rawPath?.Trim() ?? string.Empty;
            if (path.Length == 0
                || path.Length > MaxPathChars
                || Path.IsPathRooted(path)
                || path.Split('/', '\\').Contains("..")
                || path.Contains('\0')
                || seen.Contains(path))
            {
                continue;
            }

            if (excluded.Contains(path))
            {
                continue;
            }

            seen.Add(path);
            effective.Add(path);
        }

        return effective.Take(MaxStagedPaths).ToList();
    }

    private static string[] BuildAddArguments(IReadOnlyList<string> paths)
    {
        var arguments = new List<string>(paths.Count + 2) { "add", "--" };
        foreach (var path in paths)
        {
            arguments.Add($":(literal){path}");
        }

        return arguments.ToArray();
    }

    private static bool IsValidCommitMessage(string message) =>
        message.Length > 0
        && message.Length <= MaxCommitMessageChars
        && !message.Any(c => char.IsControl(c) && c is not ('\n' or '\t' or '\r'));

    private static bool IsGitExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        var fileName = Path.GetFileName(executable.Trim());
        return fileName.Equals("git", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("git.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static byte[] ReadBoundedBytes(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        using var output = new MemoryStream(Math.Min(maxBytes, 81920));
        var buffer = new byte[Math.Min(maxBytes, 81920)];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (output.Length > maxBytes - read)
            {
                throw new IOException("index_too_large");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static bool DictionariesEqual(byte[] left, byte[] right)
    {
        return left.AsSpan().SequenceEqual(right);
    }

    private static bool DictionariesEqual(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) || !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<string?> HashWorktreeFileOrNullAsync(
        string repositoryRoot,
        string relativePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var fullPath = Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            await using var stream = File.OpenRead(fullPath);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return MissingHash;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _ = error;
            return null;
        }
    }
}
