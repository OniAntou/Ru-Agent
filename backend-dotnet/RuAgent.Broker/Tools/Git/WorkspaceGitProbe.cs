using System.Globalization;
using RuAgent.Broker.Tools.Terminal;

namespace RuAgent.Broker.Tools.Git;

/// <summary>
/// Fixed-argv, read-only Git reads for Host prompt context. The Host never chooses an
/// argument, so no call here can select a write verb or a path outside the run's root.
/// Every command reuses the Broker's hardened git channel: sanitized child environment,
/// hooks and fsmonitor disabled, no lazy fetch, fixed timeout and output cap.
/// </summary>
public sealed class WorkspaceGitProbe
{
    private const int TimeoutMs = 5_000;
    private const int SnapshotTimeoutMs = 8_000;
    private const int OutputLimitBytes = 128 * 1024;
    private const int MaxChangedPaths = 20;

    // No repository setting may run code during a read. These match the defenses the
    // Broker already applies to its other fixed read-only Git calls.
    private static readonly string[] Defenses =
    [
        "-c", "core.hooksPath=NUL", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false",
        "-c", "core.quotePath=false", "-c", "gc.auto=0", "-c", "maintenance.auto=false",
    ];

    private static readonly IReadOnlyDictionary<string, string> Environment =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_CONFIG_GLOBAL"] = "NUL",
            ["GIT_CONFIG_SYSTEM"] = "NUL",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_NO_LAZY_FETCH"] = "1",
            ["GIT_OPTIONAL_LOCKS"] = "0",
        };

    private readonly IGitCli _cli;

    public WorkspaceGitProbe(IGitCli? cli = null) =>
        _cli = cli ?? new StreamingGitCli(timeoutMs: TimeoutMs, outputLimitBytes: OutputLimitBytes);

    /// <summary>
    /// Reads one bounded snapshot, or null when Git answered in a way this probe cannot
    /// describe (missing executable, unexpected failure). Cancellation propagates.
    /// </summary>
    public async Task<WorkspaceGitSnapshot?> ReadAsync(string root, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        // One overall time budget for the whole snapshot: a slow command drops the
        // repository half instead of stalling prompt assembly.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(SnapshotTimeoutMs);
        try
        {
            return await ReadCoreAsync(root, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<WorkspaceGitSnapshot?> ReadCoreAsync(string root, CancellationToken cancellationToken)
    {
        // One identity read returns the repository root and the workspace prefix
        // inside it, so changed paths can be rendered workspace-relative.
        var identity = await RunWithoutFiltersAsync(
                root,
                ["rev-parse", "--show-toplevel", "--show-prefix"],
                cancellationToken)
            .ConfigureAwait(false);
        if (!identity.Succeeded)
        {
            // Exit 128 is Git's "not a repository"; anything else is unavailability.
            return identity.ExitCode == 128
                ? WorkspaceGitSnapshot.NotRepository
                : null;
        }

        var identityLines = identity.Stdout.Split('\n', StringSplitOptions.TrimEntries);
        var snapshotRoot = identityLines.ElementAtOrDefault(0);
        var prefix = identityLines.ElementAtOrDefault(1) ?? string.Empty;
        if (prefix.Length > 0 && !prefix.EndsWith('/'))
        {
            prefix += "/";
        }

        var branchResult = await RunWithoutFiltersAsync(
                root,
                ["symbolic-ref", "--quiet", "--short", "HEAD"],
                cancellationToken)
            .ConfigureAwait(false);
        string? branch = branchResult.Succeeded ? branchResult.Stdout.Trim() : null;

        var history = await RunWithoutFiltersAsync(
                root,
                ["log", "-5", "--format=%h%x09%s"],
                cancellationToken)
            .ConfigureAwait(false);
        var recentCommits = new List<string>();
        if (history.Succeeded)
        {
            foreach (var line in history.Stdout
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Take(5))
            {
                if (line.Contains('\t', StringComparison.Ordinal))
                {
                    recentCommits.Add(line);
                }
            }
        }

        string? headCommit = null;
        string? headSubject = null;
        if (recentCommits.Count > 0)
        {
            (headCommit, headSubject) = SplitCommitLine(recentCommits[0]);
        }

        // The pathspec keeps the working-tree sample inside the run's own workspace:
        // a workspace below the repository root must not report sibling directory names.
        var status = await RunAsync(
                root,
                ["status", "--porcelain=v2", "--branch", "-z", "--", "."],
                cancellationToken)
            .ConfigureAwait(false);
        var truncated = status.FailureReason == "git_output_truncated";
        var statusSample = status.Succeeded
            ? ParseStatus(status.Stdout, prefix)
            : StatusSample.Unavailable;

        return new WorkspaceGitSnapshot(
            IsRepository: true,
            Root: string.IsNullOrWhiteSpace(snapshotRoot) ? null : snapshotRoot,
            Branch: string.IsNullOrWhiteSpace(branch) ? null : branch,
            HeadCommit: headCommit,
            HeadSubject: headSubject,
            Modified: statusSample.Modified,
            Staged: statusSample.Staged,
            Untracked: statusSample.Untracked,
            Conflicted: statusSample.Conflicted,
            Ahead: statusSample.Ahead,
            Behind: statusSample.Behind,
            ChangedPaths: statusSample.Paths,
            RecentCommits: recentCommits,
            Truncated: truncated,
            StatusUnavailable: !status.Succeeded && !truncated);
    }

    private Task<GitCliResult> RunAsync(string root, string[] argv, CancellationToken cancellationToken) =>
        _cli.RunAsync(Defenses.Concat(argv).ToArray(), root, cancellationToken, Environment);

    /// <summary>
    /// Identity and history reads never render file contents, so they skip the
    /// repository filter-table discovery. Status compares the working tree and keeps
    /// the full filter defenses.
    /// </summary>
    private Task<GitCliResult> RunWithoutFiltersAsync(
        string root,
        string[] argv,
        CancellationToken cancellationToken) =>
        _cli.RunWithoutFilterDiscoveryAsync(Defenses.Concat(argv).ToArray(), root, cancellationToken, Environment);

    private static (string? Commit, string? Subject) SplitCommitLine(string line)
    {
        if (line.Length == 0)
        {
            return (null, null);
        }

        var tab = line.IndexOf('\t', StringComparison.Ordinal);
        return tab <= 0
            ? (line, null)
            : (line[..tab], line[(tab + 1)..]);
    }

    /// <summary>
    /// Counts porcelain-v2 records. A record is NUL-terminated; a rename record is
    /// followed by the original path as its own NUL field, which is skipped.
    /// </summary>
    internal static StatusSample ParseStatus(string stdout, string workspacePrefix = "")
    {
        int modified = 0, staged = 0, untracked = 0, conflicted = 0;
        int? ahead = null, behind = null;
        var paths = new List<string>();
        var tokens = stdout.Split('\0');
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.Length == 0)
            {
                continue;
            }

            if (token.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = token.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4)
                {
                    ahead = ParseTrack(parts[2]);
                    behind = ParseTrack(parts[3]);
                }

                continue;
            }

            if (token[0] == '#')
            {
                continue;
            }

            switch (token[0])
            {
                case '1':
                {
                    var fields = token.Split(' ', 9);
                    if (fields.Length == 9)
                    {
                        ApplyChange(fields[1], ref staged, ref modified);
                        AddPath(paths, fields[8], workspacePrefix);
                    }

                    break;
                }

                case '2':
                {
                    var fields = token.Split(' ', 10);
                    if (fields.Length == 10)
                    {
                        ApplyChange(fields[1], ref staged, ref modified);
                        AddPath(paths, fields[9], workspacePrefix);
                    }

                    index++; // the original path of the rename is its own NUL field
                    break;
                }

                case 'u':
                    conflicted++;
                    break;

                case '?':
                    untracked++;
                    if (token.Length > 2)
                    {
                        AddPath(paths, token[2..], workspacePrefix);
                    }

                    break;
            }
        }

        return new StatusSample(modified, staged, untracked, conflicted, ahead, behind, paths);
    }

    private static void ApplyChange(string xy, ref int staged, ref int modified)
    {
        if (xy.Length < 2)
        {
            return;
        }

        if (xy[0] != '.')
        {
            staged++;
        }

        if (xy[1] != '.')
        {
            modified++;
        }
    }

    private static int? ParseTrack(string value)
    {
        if (value.Length < 2)
        {
            return null;
        }

        return int.TryParse(value.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count
            : null;
    }

    private static void AddPath(List<string> paths, string path, string workspacePrefix)
    {
        if (paths.Count >= MaxChangedPaths || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        // Porcelain v2 paths are repository-root-relative; the prompt renders them
        // against the workspace, so strip the workspace's own prefix first.
        if (workspacePrefix.Length > 0 && path.StartsWith(workspacePrefix, StringComparison.Ordinal))
        {
            path = path[workspacePrefix.Length..];
        }

        if (path.Length > 0)
        {
            paths.Add(path);
        }
    }

    /// <summary>One status sample, already bounded. Availability is described by the caller.</summary>
    internal sealed record StatusSample(
        int Modified,
        int Staged,
        int Untracked,
        int Conflicted,
        int? Ahead,
        int? Behind,
        IReadOnlyList<string> Paths)
    {
        internal static StatusSample Unavailable { get; } = new(0, 0, 0, 0, null, null, Array.Empty<string>());
    }
}
