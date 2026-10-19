using RuAgent.Broker.Tools.Git;
using RuAgent.Broker.Tools.Terminal;

namespace RuAgent.Broker.Subagents.Workspaces;

/// <summary>Fixed, non-interactive Git operations; no checkout, hooks, filters or lazy fetch.</summary>
public sealed class SubagentGitAdapter
{
    private readonly IGitCli _cli;
    private const int MetadataOutputLimitBytes = 8 * 1024 * 1024;
    public SubagentGitAdapter(IGitCli? cli = null) => _cli = cli ?? new StreamingGitCli(
        new StreamingProcessRunner(outputLimitBytes: MetadataOutputLimitBytes, headCapacityChars: MetadataOutputLimitBytes, tailCapacityChars: 0),
        outputLimitBytes: MetadataOutputLimitBytes);

    internal Task<string> Root(string root, CancellationToken ct) => Run(root, ["rev-parse", "--show-toplevel"], ct);
    internal Task<string> Head(string root, CancellationToken ct) => Run(root, ["rev-parse", "--verify", "HEAD^{commit}"], ct);
    internal Task<string> GitDirectory(string root, CancellationToken ct) => Run(root, ["rev-parse", "--absolute-git-dir"], ct);
    internal Task<string> CommonDirectory(string root, CancellationToken ct) => Run(root, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct);
    internal Task<string> Index(string root, CancellationToken ct) => Run(root, ["ls-files", "--stage", "-z"], ct, trim: false);
    internal Task<string> Untracked(string root, CancellationToken ct) => Run(root, ["ls-files", "--others", "--exclude-standard", "-z"], ct, trim: false);
    internal Task<string> Sparse(string root, CancellationToken ct) => Run(root, ["config", "--bool", "--get", "core.sparseCheckout"], ct, missingAllowed: true);
    internal Task<string> Branch(string root, CancellationToken ct) => Run(root, ["symbolic-ref", "--quiet", "HEAD"], ct, missingAllowed: true);
    internal Task<string> Add(string parent, string child, string head, CancellationToken ct) =>
        Run(parent, ["worktree", "add", "--no-checkout", "--detach", "--", child, head], ct);
    internal Task<string> InitializeIndex(string child, CancellationToken ct) => Run(child, ["read-tree", "HEAD"], ct);

    private async Task<string> Run(string root, string[] command, CancellationToken ct, bool trim = true, bool missingAllowed = false)
    {
        string[] defenses = ["-c", "core.hooksPath=NUL", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false",
            "-c", "core.quotePath=false", "-c", "gc.auto=0", "-c", "maintenance.auto=false"];
        var result = await _cli.RunAsync(defenses.Concat(command).ToArray(), root, ct,
            new Dictionary<string, string> { ["GIT_CONFIG_GLOBAL"] = "NUL", ["GIT_CONFIG_SYSTEM"] = "NUL",
                ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_NO_LAZY_FETCH"] = "1", ["GIT_OPTIONAL_LOCKS"] = "0" }).ConfigureAwait(false);
        if (!result.Succeeded && !(missingAllowed && result.ExitCode == 1))
            throw new SubagentWorkspaceException(result.FailureReason == "git_output_truncated"
                ? "subagent_snapshot_too_large" : "subagent_git_failed");
        return trim ? result.Stdout.Trim() : result.Stdout;
    }
}
