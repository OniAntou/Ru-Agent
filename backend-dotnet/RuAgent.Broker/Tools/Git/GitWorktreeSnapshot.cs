using System.Security.Cryptography;
using System.Text;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools.Git;

/// <summary>
/// Captures a bounded, content-free worktree snapshot through fixed read-only Git
/// invocations. Any unexpected Git or filesystem failure yields <c>null</c>: a failed
/// capture is an unknown state and is never reported as a clean worktree. Observed file
/// hashes cover only status-active paths plus caller-supplied prior paths, capped at
/// <see cref="MaxHashedFiles"/>; raw file contents are never included.
/// </summary>
public static class GitWorktreeSnapshot
{
    public const int MaxHashedFiles = 500;

    public static async Task<RunGitWorktreeSnapshot?> CaptureAsync(
        string repositoryRoot,
        IGitCli cli,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? additionalHashPaths = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return null;
        }

        var head = await cli.RunAsync(new[] { "rev-parse", "HEAD" }, repositoryRoot, cancellationToken);
        string? headOid;
        if (head.Succeeded)
        {
            headOid = head.Stdout.Trim();
            if (headOid.Length != 40)
            {
                return null;
            }
        }
        else if (IsUnknownRevision(head))
        {
            headOid = null;
        }
        else
        {
            return null;
        }

        var symref = await cli.RunAsync(
            new[] { "symbolic-ref", "--quiet", "--short", "HEAD" },
            repositoryRoot,
            cancellationToken);
        bool isDetached;
        string branchName;
        if (symref.Succeeded)
        {
            branchName = symref.Stdout.Trim();
            isDetached = false;
            if (branchName.Length == 0)
            {
                return null;
            }
        }
        else if (headOid is not null && IsRefFailure(symref))
        {
            branchName = "(detached)";
            isDetached = true;
        }
        else
        {
            return null;
        }

        var status = await cli.RunAsync(
            new[] { "status", "--porcelain=v2", "--branch" },
            repositoryRoot,
            cancellationToken);
        if (!status.Succeeded)
        {
            return null;
        }

        var parsed = GitStatusParser.Parse(status.Stdout);

        var stagedCheck = await cli.RunAsync(
            new[] { "diff", "--cached", "--quiet" },
            repositoryRoot,
            cancellationToken);
        bool hasStagedChanges;
        if (stagedCheck.ExitCode == 0)
        {
            hasStagedChanges = false;
        }
        else if (stagedCheck.ExitCode == 1)
        {
            hasStagedChanges = true;
        }
        else
        {
            return null;
        }

        var indexListing = await cli.RunAsync(new[] { "ls-files", "-s" }, repositoryRoot, cancellationToken);
        if (!indexListing.Succeeded)
        {
            return null;
        }

        var indexHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(indexListing.Stdout))).ToLowerInvariant();

        var hashPaths = new List<string>();
        foreach (var entry in parsed.Entries)
        {
            if (!hashPaths.Contains(entry.Path))
            {
                hashPaths.Add(entry.Path);
            }
        }

        var hashesTruncated = false;
        if (additionalHashPaths is not null)
        {
            foreach (var path in additionalHashPaths)
            {
                if (hashPaths.Contains(path) || hashPaths.Count >= MaxHashedFiles)
                {
                    hashesTruncated |= hashPaths.Count >= MaxHashedFiles && !hashPaths.Contains(path);
                    continue;
                }

                hashPaths.Add(path);
            }
        }

        if (hashPaths.Count > MaxHashedFiles)
        {
            hashPaths.RemoveRange(MaxHashedFiles, hashPaths.Count - MaxHashedFiles);
            hashesTruncated = true;
        }

        var observedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in hashPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = await HashFileOrMissingAsync(repositoryRoot, path, cancellationToken);
            if (hash is null)
            {
                return null;
            }

            observedHashes[path] = hash;
        }

        return new RunGitWorktreeSnapshot(
            RepositoryRoot: repositoryRoot,
            HeadOid: headOid,
            BranchName: branchName,
            IsDetached: isDetached,
            IndexHash: indexHash,
            HasStagedChanges: hasStagedChanges,
            ManagedCommitEligible: !hasStagedChanges,
            Entries: parsed.Entries,
            ObservedFileHashes: observedHashes,
            StatusTruncated: parsed.Truncated,
            FileHashesTruncated: hashesTruncated,
            CapturedAtUtc: DateTimeOffset.UtcNow);
    }

    private static async Task<string?> HashFileOrMissingAsync(string root, string relativePath, CancellationToken cancellationToken)
    {
        try
        {
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            await using var stream = File.OpenRead(fullPath);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return "missing";
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

    private static bool IsUnknownRevision(GitCliResult result) =>
        result.Stderr.Contains("unknown revision", StringComparison.OrdinalIgnoreCase)
        || result.Stderr.Contains("ambiguous argument", StringComparison.OrdinalIgnoreCase);

    private static bool IsRefFailure(GitCliResult result) =>
        result.Stderr.Contains("not a valid ref", StringComparison.OrdinalIgnoreCase)
        || result.FailureReason.StartsWith("git_exit_", StringComparison.Ordinal);
}
