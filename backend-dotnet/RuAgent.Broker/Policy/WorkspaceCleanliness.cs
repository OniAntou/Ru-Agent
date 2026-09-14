using System.Collections.Concurrent;
using System.Diagnostics;

namespace RuAgent.Broker.Policy;

/// <summary>
/// Answers whether a workspace has no tracked modifications. Used only to
/// unlock the opt-in trusted preset: anything unknown (missing git, errors,
/// timeouts) reports dirty so evaluation fails closed.
/// </summary>
public interface IWorkspaceCleanlinessProvider
{
    bool IsWorkspaceClean(string canonicalWorkspaceRoot);
}

/// <summary>
/// Git-backed cleanliness probe. Only exit codes are observed
/// (<c>rev-parse</c>, <c>diff --quiet</c>, <c>diff --cached --quiet</c>), so no
/// command output is ever read and no stream deadlock is possible. Results are
/// cached briefly per root; a dirty repo stays dirty for at most the TTL.
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

    private static bool ProbeClean(string root) =>
        RunGit(root, "rev-parse --is-inside-work-tree") == 0
        && RunGit(root, "diff --quiet") == 0
        && RunGit(root, "diff --cached --quiet") == 0;

    private static int RunGit(string workingDirectory, string arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (!process.Start())
            {
                return -1;
            }

            process.StandardOutput.ReadToEnd();
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

            return process.ExitCode;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return -1;
        }
    }

    private sealed record CacheEntry(DateTimeOffset At, bool Clean);
}
