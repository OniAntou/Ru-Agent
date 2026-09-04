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

    public CommandAnalysis Analyze(string? resolvedExecutable, IReadOnlyList<string> argv)
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

        return baseName switch
        {
            "git" => _git.Analyze(resolvedExecutable, argv),
            "dotnet" => _dotnet.Analyze(resolvedExecutable, argv),
            "python" or "python3" or "py" or "pytest" or "ruff" or "mypy" or "pip" or "pip3" =>
                _python.Analyze(resolvedExecutable, argv),
            "rg" => _ripgrep.Analyze(resolvedExecutable, argv),
            _ => Unknown("unrecognized_executable"),
        };
    }

    private static CommandAnalysis Unknown(string reasonCode) =>
        new(Version, TerminalOperationClass.Unknown, "none", reasonCode);
}
