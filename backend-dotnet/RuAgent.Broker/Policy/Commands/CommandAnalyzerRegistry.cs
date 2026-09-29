using System.Collections.Frozen;
using RuAgent.Protocol;

namespace RuAgent.Broker.Policy.Commands;

/// <summary>
/// Routes a resolved executable to its versioned family analyzer. Unrecognized
/// executables produce a typed Unknown verdict that Standard always asks on.
/// </summary>
public sealed class CommandAnalyzerRegistry
{
    public const string Version = "2";

    private readonly GitCommandAnalyzer _git = new();
    private readonly DotnetCommandAnalyzer _dotnet = new();
    private readonly PythonCommandAnalyzer _python = new();
    private readonly RipgrepCommandAnalyzer _ripgrep = new();

    public IReadOnlyList<ICommandAnalyzer> Analyzers { get; } =
    [
        new GitCommandAnalyzer(),
        new DotnetCommandAnalyzer(),
        new PythonCommandAnalyzer(),
        new RipgrepCommandAnalyzer(),
    ];

    // Pure diagnostics flags have no side effects for the executable families
    // this registry recognizes: `git --version` and `python --version` used to
    // ask on every call. The shortcut is deliberately limited to those known
    // families — an arbitrary PATH binary with `--version`/`--help`/`-h` stays
    // Unknown and asks, because its identity gives no reason to trust it.
    // Downstream checks (path-qualified executable, cwd, network, argv paths)
    // still apply. Shell scripts keep fail-closed and never take this path.
    private static readonly FrozenSet<string> DiagnosticsExecutables =
        new[]
        {
            "git", "dotnet", "python", "python3", "py",
            "pytest", "ruff", "mypy", "pip", "pip3", "rg",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public CommandAnalysis Analyze(string? resolvedExecutable, IReadOnlyList<string> argv, bool allowDiagnosticsAuto = true)
    {
        if (string.IsNullOrWhiteSpace(resolvedExecutable))
        {
            return Unknown("missing_executable");
        }

        var baseName = GitCommandAnalyzer.BaseName(resolvedExecutable);
        if (baseName is "rm" or "del" or "erase")
        {
            return new(Version, TerminalOperationClass.DirectDelete, "none", "terminal_direct_delete");
        }

        if (allowDiagnosticsAuto
            && DiagnosticsExecutables.Contains(baseName)
            && IsDiagnosticsOnly(argv))
        {
            return new(Version, TerminalOperationClass.ReadOnly, "none", "diagnostics_flag");
        }

        var analysis = baseName switch
        {
            "git" => _git.Analyze(resolvedExecutable, argv),
            "dotnet" => _dotnet.Analyze(resolvedExecutable, argv),
            "python" or "python3" or "py" or "pytest" or "ruff" or "mypy" or "pip" or "pip3" =>
                _python.Analyze(resolvedExecutable, argv),
            "rg" => _ripgrep.Analyze(resolvedExecutable, argv),
            _ => Unknown("unrecognized_executable"),
        };

        if (analysis.OperationClass == TerminalOperationClass.Unknown
            && baseName is "git" or "dotnet" or "python" or "python3" or "py"
                or "pytest" or "ruff" or "mypy" or "pip" or "pip3")
        {
            return analysis with
            {
                OperationClass = TerminalOperationClass.ExternalMutation,
                ReasonCode = "recognized_executable_unclassified_form",
            };
        }

        return analysis;
    }

    private static CommandAnalysis Unknown(string reasonCode) =>
        new(Version, TerminalOperationClass.Unknown, "none", reasonCode);

    private static bool IsDiagnosticsOnly(IReadOnlyList<string> argv) =>
        argv.Count == 1
        && (string.Equals(argv[0], "--version", StringComparison.Ordinal)
            || string.Equals(argv[0], "--help", StringComparison.Ordinal)
            || string.Equals(argv[0], "-h", StringComparison.Ordinal));
}
