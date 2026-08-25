using RuAgent.Protocol;

namespace RuAgent.Broker.Policy.Commands;

/// <summary>
/// Recognized ripgrep forms. Plain search and list-files allow; replacement and
/// preprocessor flags ask because they alter output or execute external programs.
/// </summary>
public sealed class RipgrepCommandAnalyzer : ICommandAnalyzer
{
    public const string AnalyzerVersion = "1";

    public string Version => AnalyzerVersion;

    public CommandAnalysis Analyze(string resolvedExecutable, IReadOnlyList<string> argv)
    {
        if (GitCommandAnalyzer.BaseName(resolvedExecutable) is not "rg")
        {
            return new(AnalyzerVersion, TerminalOperationClass.Unknown, "none", "ripgrep_analyzer_not_applicable");
        }

        for (var i = 0; i < argv.Count; i++)
        {
            var argument = argv[i];
            if (argument is "-r" || argument.StartsWith("--replace", StringComparison.Ordinal))
            {
                return new(AnalyzerVersion, TerminalOperationClass.Unknown, "none", "ripgrep_replacement_requires_approval");
            }

            if (argument is "--pre" or "--pre-glob"
                || (argument.StartsWith("--pre=", StringComparison.Ordinal)))
            {
                return new(AnalyzerVersion, TerminalOperationClass.Unknown, "none", "ripgrep_preprocessor_requires_approval");
            }

            // Flags with separate values must skip the value token.
            if (argument is "--replace" or "--pre" or "--pre-glob")
            {
                i++;
            }
        }

        return new(AnalyzerVersion, TerminalOperationClass.ReadOnly, "none", "ripgrep_search");
    }
}
