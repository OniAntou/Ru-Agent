using RuAgent.Broker.Tools.Git;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness.Prompting;

/// <summary>
/// Reports a bounded git state for the prompt environment. The model needs to know
/// that the workspace holds work in progress before it edits anything; the probe
/// returns counts only, never file names, so the prompt stays free of paths. Counts
/// come from the Broker's porcelain-v2 parser instead of a second ad-hoc parser.
/// </summary>
internal static class WorkspaceGitProbe
{
    private const int TimeoutMs = 5_000;
    private const int OutputLimitBytes = 256 * 1024;

    // The Broker channel, not Process directly: it sanitizes the inherited env,
    // pins fsmonitor, bounds output, and streams stdout/stderr with a real timeout
    // instead of blocking on ReadToEnd before WaitForExit.
    private static readonly IGitCli Git = new StreamingGitCli(
        outputLimitBytes: OutputLimitBytes,
        timeoutMs: TimeoutMs);

    public static Task<string> DescribeAsync(string root, CancellationToken cancellationToken = default) =>
        DescribeAsync(root, Git, cancellationToken);

    internal static async Task<string> DescribeAsync(
        string root,
        IGitCli git,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return "unknown";
        }

        var top = await git.RunAsync(["rev-parse", "--show-toplevel"], root, cancellationToken).ConfigureAwait(false);
        if (top.ExitCode != 0)
        {
            return IsNotARepository(top) ? "not_a_repository" : "unknown";
        }

        var status = await git.RunAsync(["status", "--porcelain=v2", "--branch"], root, cancellationToken).ConfigureAwait(false);
        if (status.ExitCode != 0)
        {
            return "unknown";
        }

        var parsed = GitStatusParser.Parse(status.Stdout);
        var untracked = parsed.Entries.Count(entry => entry.Kind == RunGitEntryKind.Untracked);
        var changed = parsed.Entries.Count - untracked;
        if (changed == 0 && untracked == 0)
        {
            return parsed.Truncated ? "unknown" : "clean";
        }

        var suffix = parsed.Truncated ? "+" : string.Empty;
        return $"dirty ({changed}{suffix} changed, {untracked}{suffix} untracked)";
    }

    /// <summary>
    /// A timeout, a filter guard failure or a truncated answer is "unknown", not
    /// "not a repository": the difference matters because the prompt would otherwise
    /// claim the workspace has no repository history.
    /// </summary>
    private static bool IsNotARepository(GitCliResult result) =>
        string.Equals(result.FailureReason, "git_exit_128", StringComparison.Ordinal)
        && result.Stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase);
}
