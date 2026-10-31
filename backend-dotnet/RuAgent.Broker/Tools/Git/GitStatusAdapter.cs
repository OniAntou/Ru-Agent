using System.Text.Json.Nodes;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools.Git;

/// <summary>
/// The outcome of one bounded Git status read. A successful read is an answer even
/// when the workspace is not a repository; only a Git invocation that could not
/// produce a trustworthy state fails.
/// </summary>
public sealed record GitStatusReport(
    JsonObject? Output,
    string? ErrorCode,
    string? ErrorHint)
{
    public bool Succeeded => Output is not null;

    public static GitStatusReport Success(JsonObject output) => new(output, null, null);

    public static GitStatusReport Failure(string errorCode, string errorHint) =>
        new(null, errorCode, errorHint);
}

/// <summary>
/// Read-only, bounded report of the workspace's Git state. One fixed
/// <c>git status --porcelain=v2 --branch</c> invocation runs through
/// <see cref="IGitCli"/> and the existing <see cref="GitStatusParser"/>; nothing here
/// stages, commits, diffs, fetches or runs a repository-configured program.
///
/// The pathspec is the workspace subtree, so a workspace that is a subdirectory of a
/// larger repository never reports paths outside it. Git renders pathspec-limited
/// status relative to the working directory, which is exactly workspace-relative.
/// </summary>
public sealed class GitStatusAdapter
{
    internal const string NotARepositoryNotice =
        "The workspace is not inside a Git repository, so there is no branch or status to report.";

    internal const string CleanNotice =
        "The working tree has no staged, unstaged, untracked or unmerged changes.";

    internal const string TruncatedNotice =
        "The status entry cap was reached; the reported lists are incomplete.";

    internal const string UnparsedNotice =
        "Some Git status lines could not be interpreted; the reported lists may be incomplete.";

    // Read-only Git defenses beyond the ones IGitCli already pins: force the two
    // status settings a repository or global config could change, so paths stay
    // workspace-relative and ahead/behind is computed whenever an upstream exists.
    private static readonly string[] StatusArguments =
    [
        "-c", "status.relativePaths=true",
        "status",
        "--porcelain=v2",
        "--branch",
        "--ahead-behind",
        "--untracked-files=normal",
        "--",
        ".",
    ];

    // LC_ALL=C keeps Git diagnostics stable English, so "not a git repository" is a
    // deterministic answer rather than a matched localized string. Paths are
    // unaffected: porcelain escapes or emits their bytes, never localized text.
    private static readonly IReadOnlyDictionary<string, string> ReadEnvironment =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LC_ALL"] = "C",
        };

    private readonly IGitCli _cli;

    public GitStatusAdapter(IGitCli? cli = null) => _cli = cli ?? new StreamingGitCli();

    public async Task<GitStatusReport> ExecuteAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var result = await _cli.RunAsync(
            StatusArguments,
            workspaceRoot,
            cancellationToken,
            ReadEnvironment).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            if (IsNotARepository(result))
            {
                // "No repository" is a fact about the workspace, not a failed tool
                // call: answering it keeps a legitimate question out of the
                // recovery-note budget that failed receipts consume.
                return GitStatusReport.Success(new JsonObject
                {
                    ["is_repository"] = false,
                    ["notice"] = NotARepositoryNotice,
                });
            }

            return StatusFailure(result);
        }

        return GitStatusReport.Success(BuildReport(GitStatusParser.Parse(result.Stdout)));
    }

    internal static JsonObject BuildReport(RunGitStatusParseResult parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        var staged = new JsonArray();
        var unstaged = new JsonArray();
        var untracked = new JsonArray();
        var unmerged = new JsonArray();
        foreach (var entry in parsed.Entries)
        {
            switch (entry.Kind)
            {
                case RunGitEntryKind.Untracked:
                    untracked.Add(entry.Path);
                    break;
                case RunGitEntryKind.Unmerged:
                    // Conflicts are neither staged nor unstaged work; keeping them
                    // apart stops a resolved-looking "U" code from reading as a
                    // normal tracked change.
                    unmerged.Add(new JsonObject
                    {
                        ["path"] = entry.Path,
                        ["index_code"] = entry.IndexCode,
                        ["worktree_code"] = entry.WorktreeCode,
                    });
                    break;
                default:
                    if (entry.HasStagedChanges)
                    {
                        staged.Add(ChangeEntry(entry, entry.IndexCode));
                    }

                    if (entry.HasUnstagedChanges)
                    {
                        unstaged.Add(ChangeEntry(entry, entry.WorktreeCode));
                    }

                    break;
            }
        }

        var branch = parsed.Branch;
        var clean = staged.Count == 0
            && unstaged.Count == 0
            && untracked.Count == 0
            && unmerged.Count == 0
            && parsed.Errors.Count == 0;
        var output = new JsonObject
        {
            ["is_repository"] = true,
            ["branch"] = branch.IsDetached ? (string?)null : branch.HeadRef,
            ["detached"] = branch.IsDetached,
            ["head_oid"] = branch.HeadOid,
            ["upstream"] = branch.Upstream,
            ["ahead"] = branch.AheadCount is { } ahead ? JsonValue.Create(ahead) : null,
            ["behind"] = branch.BehindCount is { } behind ? JsonValue.Create(behind) : null,
            ["staged"] = staged,
            ["unstaged"] = unstaged,
            ["untracked"] = untracked,
            ["unmerged"] = unmerged,
            ["truncated"] = parsed.Truncated,
            ["unparsed_lines"] = parsed.Errors.Count,
            ["clean"] = clean,
        };

        if (parsed.Truncated)
        {
            output["notice"] = TruncatedNotice;
        }
        else if (parsed.Errors.Count > 0)
        {
            output["notice"] = UnparsedNotice;
        }
        else if (clean)
        {
            output["notice"] = CleanNotice;
        }

        return output;
    }

    private static JsonObject ChangeEntry(RunGitStatusEntry entry, string code)
    {
        var node = new JsonObject
        {
            ["path"] = entry.Path,
            ["code"] = code,
        };
        if (entry.OriginalPath is { Length: > 0 } originalPath)
        {
            node["original_path"] = originalPath;
        }

        return node;
    }

    private static bool IsNotARepository(GitCliResult result) =>
        result.Stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase);

    private static GitStatusReport StatusFailure(GitCliResult result) => result.FailureReason switch
    {
        "git_output_truncated" => GitStatusReport.Failure(
            "git_status_too_large",
            "Git status output exceeded the bounded read. The workspace has a very large change set; report that the full status could not be read."),
        "git_timed_out" => GitStatusReport.Failure(
            "git_status_timed_out",
            "Git status timed out. Do not repeat the same call immediately; inspect the repository or report the timeout."),
        _ when result.ExitCode < 0 => GitStatusReport.Failure(
            "git_unavailable",
            "Git could not start for this workspace. Report that Git state cannot be read here; do not retry the same call."),
        _ => GitStatusReport.Failure(
            "git_status_failed",
            $"Git status failed ({result.FailureReason}). Do not retry the same call unchanged; verify the workspace is a readable Git worktree, or report the failure."),
    };
}
