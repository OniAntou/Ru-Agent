using RuAgent.Protocol;

namespace RuAgent.Broker.Policy.Commands;

/// <summary>
/// Recognized dotnet SDK forms. test/build and pure diagnostics allow; restore,
/// packaging, tooling, publishing, and project creation ask; run/exec fail closed
/// to Unknown because they execute arbitrary project code.
/// </summary>
public sealed class DotnetCommandAnalyzer : ICommandAnalyzer
{
    public const string AnalyzerVersion = "1";

    public string Version => AnalyzerVersion;

    public CommandAnalysis Analyze(string resolvedExecutable, IReadOnlyList<string> argv)
    {
        if (GitCommandAnalyzer.BaseName(resolvedExecutable) is not "dotnet")
        {
            return new(AnalyzerVersion, TerminalOperationClass.Unknown, "none", "dotnet_analyzer_not_applicable");
        }

        if (argv.Count == 0)
        {
            return new(AnalyzerVersion, TerminalOperationClass.Unknown, "none", "missing_verb");
        }

        var verb = argv[0];
        switch (verb)
        {
            case "test":
            case "vstest":
                return new(AnalyzerVersion, TerminalOperationClass.Test, "none", "dotnet_test");

            case "build":
                return new(AnalyzerVersion, TerminalOperationClass.Build, "none", "dotnet_build");

            case "--version":
            case "--info":
            case "--list-sdks":
            case "--list-runtimes":
            case "-h":
            case "--help":
                return new(AnalyzerVersion, TerminalOperationClass.ReadOnly, "none", "dotnet_diagnostics");

            case "restore":
                return new(AnalyzerVersion, TerminalOperationClass.ExternalMutation, "declared", "dotnet_restore");

            case "publish":
            case "push":
            case "pack":
            case "new":
            case "add":
            case "remove":
            case "tool":
            case "workload":
            case "store":
            case "format":
                return new(AnalyzerVersion, TerminalOperationClass.ExternalMutation, "declared", "dotnet_external_mutation");

            default:
                return new(AnalyzerVersion, TerminalOperationClass.Unknown, "none", "unknown_dotnet_verb");
        }
    }
}
