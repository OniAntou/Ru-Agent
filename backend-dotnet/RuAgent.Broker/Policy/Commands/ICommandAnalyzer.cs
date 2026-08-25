using RuAgent.Protocol;

namespace RuAgent.Broker.Policy.Commands;

/// <summary>
/// One versioned analyzer verdict for a resolved command intent.
/// </summary>
public sealed record CommandAnalysis(
    string AnalyzerVersion,
    TerminalOperationClass OperationClass,
    string NetworkIntent,
    string ReasonCode);

public interface ICommandAnalyzer
{
    /// <summary>Analyzer contract version; bump when a matrix changes meaning.</summary>
    string Version { get; }

    /// <summary>
    /// Classifies one resolved command. Parsing failures are total: they return
    /// typed Unknown/Ask verdicts and never an Allow.
    /// </summary>
    CommandAnalysis Analyze(string resolvedExecutable, IReadOnlyList<string> argv);
}
