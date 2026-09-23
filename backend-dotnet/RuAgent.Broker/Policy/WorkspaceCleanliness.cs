using System.Collections.Concurrent;
using System.Diagnostics;

namespace RuAgent.Broker.Policy;

/// <summary>
/// Answers whether a workspace has no tracked modifications. Used only to
/// unlock the opt-in standard clean-workspace relaxation: anything unknown
/// (missing git, errors, timeouts) reports dirty so evaluation fails closed.
/// </summary>
public interface IWorkspaceCleanlinessProvider
{
    bool IsWorkspaceClean(string canonicalWorkspaceRoot);
}

/// <summary>
/// Git-backed cleanliness probe. Only exit codes and whether the porcelain status is
/// empty are observed, so no command output is ever parsed from a stream and no
/// stream deadlock is possible. Results are cached briefly per root; a dirty repo
/// stays dirty for at most the TTL.
/// </summary>
public sealed class GitWorkspaceCleanlinessProvider : IWorkspaceCleanlinessProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public bool IsWorkspaceClean(string canonicalWorkspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(canonicalWorkspaceRoot))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(canonicalWorkspaceRoot, out var hit) && now - hit.At < CacheTtl)
        {
            return hit.Clean;
        }

        var clean = ProbeClean(canonicalWorkspaceRoot);
        _cache[canonicalWorkspaceRoot] = new CacheEntry(now, clean);
        return clean;
    }

    /// <summary>
    /// A workspace is clean only when its porcelain status reports nothing at all.
    /// <c>diff --quiet</c> plus <c>diff --cached --quiet</c> is not sufficient: neither
    /// sees an untracked file, so a freshly created and never-committed <c>.env</c> or
    /// credential file would have counted as clean and unlocked the standard
    /// relaxation over a workspace that is in fact carrying new secrets.
    /// </summary>
    /// <remarks>
    /// <c>core.fsmonitor=false</c> matters: the probe runs automatically on every
    /// policy ask, so a cloned repository whose <c>.git/config</c> points fsmonitor
    /// at a program must not get that program executed with the Broker's rights
    /// just because somebody opened the folder. Same reasoning as the command
    /// analyzers: configuration that can select a program never runs unattended.
    /// </remarks>
    private static bool ProbeClean(string root) =>
        RunGit(root, ["-c", "core.fsmonitor=false", "rev-parse", "--is-inside-work-tree"]) == 0
        && RunGit(root, ["-c", "core.fsmonitor=false", "status", "--porcelain"], observeEmptyOutput: true) == 0;

    private static int RunGit(string workingDirectory, string[] arguments, bool observeEmptyOutput = false)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                return -1;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Best-effort kill only.
                }

                return -1;
            }

            if (process.ExitCode != 0)
            {
                return process.ExitCode;
            }

            return !observeEmptyOutput || string.IsNullOrWhiteSpace(stdout) ? 0 : 1;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return -1;
        }
    }

    private sealed record CacheEntry(DateTimeOffset At, bool Clean);
}
