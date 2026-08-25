using RuAgent.Protocol;

namespace RuAgent.Broker.Policy.Commands;

/// <summary>
/// Recognized Python ecosystem forms: pytest, ruff check, mypy — directly or through
/// <c>python -m</c>. Arbitrary scripts, inline code, other module execution, pip
/// installs, fixing linters, and interactive debugger flags never allow.
/// </summary>
public sealed class PythonCommandAnalyzer : ICommandAnalyzer
{
    public const string AnalyzerVersion = "1";

    public string Version => AnalyzerVersion;

    public CommandAnalysis Analyze(string resolvedExecutable, IReadOnlyList<string> argv)
    {
        var baseName = GitCommandAnalyzer.BaseName(resolvedExecutable);
        return baseName switch
        {
            "pytest" => AnalyzePytest(argv),
            "ruff" => AnalyzeRuff(argv),
            "mypy" => Lint(),
            "pip" or "pip3" => PipInstall("python_pip_install"),
            "python" or "python3" or "py" => AnalyzePythonModule(argv),
            _ => new(AnalyzerVersion, TerminalOperationClass.Unknown, "none", "python_analyzer_not_applicable"),
        };
    }

    private CommandAnalysis AnalyzePythonModule(IReadOnlyList<string> argv)
    {
        for (var i = 0; i + 1 < argv.Count; i++)
        {
            if (!string.Equals(argv[i], "-m", StringComparison.Ordinal))
            {
                continue;
            }

            return argv[i + 1] switch
            {
                var module when module.StartsWith("pytest", StringComparison.Ordinal) => Test(),
                "ruff" => AnalyzeRuff(argv.Skip(i + 2).ToArray()),
                "mypy" => Lint(),
                "pip" or "pip3" => PipInstall("python_pip_install"),
                _ => new(AnalyzerVersion, TerminalOperationClass.Unknown, "none", "python_module_execution_asks"),
            };
        }

        return new(AnalyzerVersion, TerminalOperationClass.Unknown, "none", "python_arbitrary_execution_asks");
    }

    private CommandAnalysis AnalyzePytest(IReadOnlyList<string> argv)
    {
        if (argv.Any(argument => argument is "--pdb" or "--trace" or "-p" && IsPdbPlugin(argument, argv)))
        {
            return new(AnalyzerVersion, TerminalOperationClass.Unknown, "none", "pytest_interactive_asks");
        }

        return Test();
    }

    private static bool IsPdbPlugin(string argument, IReadOnlyList<string> argv)
    {
        // Only "-p no:cacheprovider"-style non-interactive plugin selection stays safe.
        return !(argument == "-p" && argv.Contains("no:cacheprovider", StringComparer.Ordinal));
    }

    private static CommandAnalysis AnalyzeRuff(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0 || !string.Equals(argv[0], "check", StringComparison.Ordinal))
        {
            return new(AnalyzerVersion, TerminalOperationClass.Unknown, "none", "ruff_subcommand_asks");
        }

        if (argv.Any(argument => argument is "--fix" or "--unsafe-fixes" or "--fix-only"))
        {
            return new(AnalyzerVersion, TerminalOperationClass.ExternalMutation, "none", "ruff_fix_mutates");
        }

        return Lint();
    }

    private static CommandAnalysis Test() =>
        new(AnalyzerVersion, TerminalOperationClass.Test, "none", "python_test");

    private static CommandAnalysis Lint() =>
        new(AnalyzerVersion, TerminalOperationClass.Lint, "none", "python_lint");

    private static CommandAnalysis PipInstall(string reasonCode) =>
        new(AnalyzerVersion, TerminalOperationClass.ExternalMutation, "declared", reasonCode);
}
