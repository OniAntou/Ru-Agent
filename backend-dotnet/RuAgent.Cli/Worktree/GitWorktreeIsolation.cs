using System.Diagnostics;
using System.Text;

namespace RuAgent.Cli.Worktree;

/// <summary>
/// A git worktree created for one run. The caller keeps it after the run so the
/// diff can be reviewed and merged; removing it is an explicit
/// <c>git worktree remove</c>, never an automatic cleanup that could delete work.
/// </summary>
public sealed record WorktreeLease(
    string RepositoryRoot,
    string WorktreePath,
    string WorkspacePath,
    string Branch);

/// <summary>
/// Creates an isolated git worktree for a headless run so the agent edits a copy
/// at HEAD instead of the caller's working tree. Only committed state is copied:
/// uncommitted parent changes are not carried, and the parent stays untouched.
/// Failures are named codes; nothing is guessed or silently substituted.
/// </summary>
public static class GitWorktreeIsolation
{
    /// <summary>Overrides where worktrees are created (absolute path).</summary>
    public const string RootVariable = "RU_WORKTREE_ROOT";

    private const int MaximumErrorChars = 300;
    private static readonly TimeSpan InspectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromMinutes(2);

    public static async Task<(WorktreeLease? Lease, string? Error)> CreateAsync(
        string workspace,
        string? rootOverride,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        var fullWorkspace = Path.GetFullPath(workspace);
        if (!Directory.Exists(fullWorkspace))
        {
            return (null, "worktree_workspace_missing: the workspace directory does not exist.");
        }

        var inspect = await RunGitAsync(
            fullWorkspace, ["rev-parse", "--show-toplevel"], InspectTimeout, cancellationToken).ConfigureAwait(false);
        if (inspect.ExitCode != 0)
        {
            return (null, inspect.Missing
                ? "worktree_git_missing: git was not found on PATH."
                : $"worktree_not_repository: the workspace is not inside a git working tree ({inspect.Summary}).");
        }

        var repositoryRoot = Path.GetFullPath(inspect.Stdout.Trim());
        if (!Directory.Exists(repositoryRoot))
        {
            return (null, "worktree_not_repository: git reported a working tree that does not exist.");
        }

        var relative = Path.GetRelativePath(repositoryRoot, fullWorkspace);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return (null, "worktree_not_repository: the workspace is outside the repository root.");
        }

        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        id = id[..Math.Min(24, id.Length)];
        var branch = $"ru/worktree/{id}";
        var worktreeRoot = ResolveWorktreeRoot(rootOverride);
        var name = SafeName(Path.GetFileName(repositoryRoot));
        var target = Path.Combine(worktreeRoot, $"{name}-{id}");

        try
        {
            Directory.CreateDirectory(worktreeRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"worktree_create_failed: cannot create the worktree root ({Truncate(ex.Message)}).");
        }

        var create = await RunGitAsync(
            repositoryRoot,
            ["worktree", "add", "-b", branch, target, "HEAD"],
            CreateTimeout,
            cancellationToken).ConfigureAwait(false);
        if (create.ExitCode != 0)
        {
            var code = create.Missing
                ? "worktree_git_missing"
                : LooksLikeMissingHead(create.Stderr)
                    ? "worktree_no_commits: the repository has no commit to branch from."
                    : "worktree_create_failed";
            return (null, $"{code}: {create.Summary}");
        }

        var worktreeWorkspace = relative == "."
            ? target
            : Path.Combine(target, relative);
        if (!Directory.Exists(worktreeWorkspace))
        {
            return (null, "worktree_create_failed: git created the worktree without the workspace subdirectory.");
        }

        return (new WorktreeLease(repositoryRoot, target, worktreeWorkspace, branch), null);
    }

    private static string ResolveWorktreeRoot(string? rootOverride)
    {
        if (!string.IsNullOrWhiteSpace(rootOverride))
        {
            return Path.GetFullPath(rootOverride);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru",
            "worktrees");
    }

    private static string SafeName(string name)
    {
        var safe = new string(name.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-').ToArray());
        return safe.Length == 0 ? "repo" : safe[..Math.Min(safe.Length, 40)];
    }

    private static bool LooksLikeMissingHead(string stderr) =>
        stderr.Contains("invalid reference: HEAD", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("not a valid object name", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("fatal: bad revision", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value)
    {
        var singleLine = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return singleLine.Length <= MaximumErrorChars ? singleLine : singleLine[..MaximumErrorChars];
    }

    private static async Task<GitResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new GitResult(-1, string.Empty, string.Empty, Missing: true);
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the timeout and the kill.
            }

            return new GitResult(-2, string.Empty, "git did not finish in time.", Missing: false);
        }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        return new GitResult(process.ExitCode, output, error, Missing: false);
    }

    private sealed record GitResult(int ExitCode, string Stdout, string Stderr, bool Missing)
    {
        public string Summary => Truncate(string.IsNullOrWhiteSpace(Stderr) ? Stdout : Stderr);
    }
}
