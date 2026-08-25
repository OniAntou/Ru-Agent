using RuAgent.Protocol;

namespace RuAgent.Broker.Policy.Commands;

/// <summary>
/// Recognized local Git forms. Reads allow; local mutations ask; push/fetch/pull/clone/remote
/// are external network mutations that never join the managed commit workflow.
/// </summary>
public sealed class GitCommandAnalyzer : ICommandAnalyzer
{
    public const string AnalyzerVersion = "1";

    public string Version => AnalyzerVersion;

    public CommandAnalysis Analyze(string resolvedExecutable, IReadOnlyList<string> argv)
    {
        if (!IsGitExecutable(resolvedExecutable))
        {
            return Unknown("git_analyzer_not_applicable");
        }

        var index = 0;
        while (index < argv.Count && argv[index].StartsWith('-'))
        {
            if (string.Equals(argv[index], "--no-optional-locks", StringComparison.Ordinal))
            {
                index++;
                break;
            }

            return Unknown("unsupported_global_flags");
        }

        if (index >= argv.Count)
        {
            return Unknown("missing_verb");
        }

        var verb = argv[index];
        var rest = argv.Skip(index + 1).ToArray();

        if (rest.Any(argument => argument.StartsWith("--output", StringComparison.Ordinal)))
        {
            return new(AnalyzerVersion, TerminalOperationClass.GitMutation, "none", "git_output_redirect");
        }

        switch (verb)
        {
            case "status":
            case "diff":
            case "log":
            case "show":
            case "rev-parse":
            case "describe":
            case "shortlog":
            case "reflog":
            case "ls-files":
            case "grep":
            case "cat-file":
                return new(AnalyzerVersion, TerminalOperationClass.ReadOnly, "none", "git_read_only");

            case "branch":
                return rest.All(IsBranchShowCurrentFlag)
                    ? new(AnalyzerVersion, TerminalOperationClass.ReadOnly, "none", "git_read_only")
                    : new(AnalyzerVersion, TerminalOperationClass.GitMutation, "none", "git_mutation");

            case "push":
            case "fetch":
            case "pull":
            case "clone":
            case "ls-remote":
            case "remote":
                return new(AnalyzerVersion, TerminalOperationClass.ExternalMutation, "declared", "git_network_external");

            default:
                return IsKnownLocalMutation(verb)
                    ? new(AnalyzerVersion, TerminalOperationClass.GitMutation, "none", "git_mutation")
                    : Unknown("unknown_git_verb");
        }
    }

    private static bool IsBranchShowCurrentFlag(string argument) =>
        string.Equals(argument, "--show-current", StringComparison.Ordinal);

    private static bool IsKnownLocalMutation(string verb) => verb switch
    {
        "commit" or "add" or "rm" or "mv" or "merge" or "rebase" or "reset" or "checkout" or
        "switch" or "restore" or "clean" or "stash" or "tag" or "cherry-pick" or "revert" or
        "am" or "apply" or "bisect" or "config" or "worktree" or "submodule" or "gc" or
        "prune" or "replace" or "notes" or "filter-branch" or "format-patch" => true,
        _ => false,
    };

    private static bool IsGitExecutable(string resolvedExecutable) =>
        BaseName(resolvedExecutable) is "git";

    internal static string BaseName(string resolvedExecutable)
    {
        var name = Path.GetFileName(resolvedExecutable.TrimEnd());
        foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^extension.Length];
            }
        }

        return name;
    }

    private static CommandAnalysis Unknown(string reasonCode) =>
        new(AnalyzerVersion, TerminalOperationClass.Unknown, "none", reasonCode);
}
