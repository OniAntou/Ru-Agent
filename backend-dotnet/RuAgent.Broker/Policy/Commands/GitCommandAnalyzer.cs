using System.Collections.Frozen;
using RuAgent.Protocol;

namespace RuAgent.Broker.Policy.Commands;

/// <summary>
/// Recognized local Git forms. Reads allow; local mutations ask; push/fetch/pull/clone/remote
/// are external network mutations that never join the managed commit workflow.
/// </summary>
public sealed class GitCommandAnalyzer : ICommandAnalyzer
{
    public const string AnalyzerVersion = "2";

    public string Version => AnalyzerVersion;

    private static readonly FrozenSet<string> InertConfigKeys =
        new[]
        {
            "user.name",
            "user.email",
            "core.quotepath",
            "core.autocrlf",
            "core.safecrlf",
            "core.ignorecase",
            "core.filemode",
            "core.abbrev",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Options that make git hand control to another program: `grep -O` spawns the
    // named command, --paginate forces the pager, and diff/textconv/filter options
    // run helpers named by repository configuration. A ReadOnly verdict is
    // auto-executable, so any of these fails closed to an approval ask.
    private static readonly string[] ProgramSelectingOptionPrefixes =
    [
        "--open-files-in-pager",
        "--ext-diff",
        "--textconv",
        "--filters",
        "--paginate",
        "-O",
    ];

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
                continue;
            }

            if (string.Equals(argv[index], "-C", StringComparison.Ordinal) && index + 1 < argv.Count)
            {
                // A directory override binds the command to a repository the
                // policy context cannot resolve: the terminal intent keeps the
                // caller's working directory, so `git -C <path>` could read or
                // mutate any repository on the machine while still looking like
                // a workspace-local read. Every override asks; callers set the
                // tool's working_directory instead.
                return Unknown("git_directory_override_requires_approval");
            }

            if (string.Equals(argv[index], "-c", StringComparison.Ordinal) && index + 1 < argv.Count)
            {
                if (!IsInertConfigOverride(argv[index + 1]))
                {
                    return Unknown("git_config_override_requires_approval");
                }

                index += 2;
                continue;
            }

            return Unknown("unsupported_global_flags");
        }

        if (index >= argv.Count)
        {
            return Unknown("missing_verb");
        }

        var verb = argv[index];
        var rest = argv.Skip(index + 1).ToArray();

        if (string.Equals(verb, "submodule", StringComparison.Ordinal))
        {
            return AnalyzeSubmodule(rest);
        }

        if (rest.Any(argument => argument.StartsWith("--output", StringComparison.Ordinal)))
        {
            return new(AnalyzerVersion, TerminalOperationClass.GitMutation, "none", "git_output_redirect");
        }

        if (rest.Any(SelectsAnotherProgram))
        {
            return Unknown("git_program_selecting_option_requires_approval");
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

            case "config":
                if (IsExternalConfigForm(rest))
                {
                    return new(AnalyzerVersion, TerminalOperationClass.ExternalMutation, "none", "git_config_external_mutation");
                }

                return IsConfigReadForm(rest)
                    ? new(AnalyzerVersion, TerminalOperationClass.ReadOnly, "none", "git_config_read")
                    : Unknown("git_config_mutation_requires_approval");

            default:
                return IsKnownLocalMutation(verb)
                    ? new(AnalyzerVersion, TerminalOperationClass.GitMutation, "none", "git_mutation")
                    : Unknown("unknown_git_verb");
        }
    }

    private static CommandAnalysis AnalyzeSubmodule(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && string.Equals(arguments[0], "status", StringComparison.Ordinal)
            ? new(AnalyzerVersion, TerminalOperationClass.ReadOnly, "none", "git_submodule_status")
            : new(AnalyzerVersion, TerminalOperationClass.ExternalMutation, "unknown", "git_submodule_requires_approval");

    private static bool IsBranchShowCurrentFlag(string argument) =>
        string.Equals(argument, "--show-current", StringComparison.Ordinal);

    private static bool SelectsAnotherProgram(string argument) =>
        ProgramSelectingOptionPrefixes.Any(prefix => argument.StartsWith(prefix, StringComparison.Ordinal));

    private static bool IsKnownLocalMutation(string verb) => verb switch
    {
        "commit" or "add" or "rm" or "mv" or "merge" or "rebase" or "reset" or "checkout" or
        "switch" or "restore" or "clean" or "stash" or "tag" or "cherry-pick" or "revert" or
        "am" or "apply" or "bisect" or "worktree" or "gc" or
        "prune" or "replace" or "notes" or "filter-branch" or "format-patch" => true,
        _ => false,
    };

    private static bool IsExternalConfigForm(IReadOnlyList<string> arguments)
    {
        // User/system configuration and arbitrary config files outlive the run
        // and can select programs (hooks, editors, credential helpers), so they
        // never take the automatic path. Repository-local configuration stays a
        // workspace-scoped GitMutation.
        foreach (var argument in arguments)
        {
            switch (argument)
            {
                case "--global":
                case "--system":
                case "--edit":
                case "-e":
                case "--file":
                case "-f":
                    return true;
            }

            if (argument.StartsWith("--file=", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Repository-local writes are never auto-executable: `git config` can set
    // keys that make later automatic git commands hand control to another
    // program (core.fsmonitor, core.hooksPath, diff.external, aliases, ...).
    // Only read forms of repository-local config keep the automatic path.
    private static readonly HashSet<string> ConfigWriteFlags = new(StringComparer.Ordinal)
    {
        "--unset",
        "--unset-all",
        "--add",
        "--replace-all",
        "--rename-section",
        "--remove-section",
    };

    private static readonly HashSet<string> ConfigReadFlags = new(StringComparer.Ordinal)
    {
        "--get",
        "--get-all",
        "--get-regexp",
        "--get-urlmatch",
        "--list",
        "-l",
        "--show-origin",
        "--show-scope",
        "--name-only",
        "--null",
        "-z",
        "--includes",
        "--no-includes",
    };

    private static bool IsConfigReadForm(IReadOnlyList<string> arguments)
    {
        var positionals = 0;
        var hasReadFlag = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (ConfigWriteFlags.Contains(argument))
            {
                return false;
            }

            if (ConfigReadFlags.Contains(argument))
            {
                hasReadFlag = true;
                continue;
            }

            // Value-taking modifiers that keep the read shape.
            if (argument is "--default" or "--type")
            {
                index++;
                continue;
            }

            if (argument.StartsWith("--default=", StringComparison.Ordinal)
                || argument.StartsWith("--type=", StringComparison.Ordinal))
            {
                continue;
            }

            if (argument is "--bool" or "--int" or "--bool-or-int" or "--path" or "--expiry-date")
            {
                continue;
            }

            if (argument.StartsWith('-'))
            {
                // Unknown flag: fail closed to approval rather than guessing.
                return false;
            }

            positionals++;
        }

        if (hasReadFlag)
        {
            return true;
        }

        // `git config <key>` reads the value; `git config <key> <value>` writes.
        return positionals <= 1;
    }

    private static bool IsInertConfigOverride(string assignment)
    {
        // `-c key=value` can point git at programs it then executes
        // (`diff.external`, `core.fsmonitor`, `core.hooksPath`, `include.path`,
        // aliases, ...). Only keys that cannot select a program keep the
        // automatic path; everything else fails closed to Unknown (ask).
        var separator = assignment.IndexOf('=');
        return separator > 0
            && InertConfigKeys.Contains(assignment[..separator]);
    }

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
