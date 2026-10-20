namespace RuApp.Services;

/// <summary>
/// Tracks transient feed identities for one AgentRunController. The caller owns
/// synchronization and resets the ledger when feed ownership changes.
/// </summary>
internal sealed class AgentRunDeltaDeduplicator
{
    private readonly HashSet<(string RunId, string StepId, long Sequence)> _assistant = [];
    private readonly HashSet<(string RunId, string ExecutionId, long Sequence)> _toolOutput = [];

    internal bool TryAcceptAssistant(string runId, string? stepId, long sequence) =>
        _assistant.Add((runId, stepId ?? string.Empty, sequence));

    internal bool TryAcceptToolOutput(string runId, string? executionId, long sequence) =>
        _toolOutput.Add((runId, executionId ?? string.Empty, sequence));

    internal void Reset()
    {
        _assistant.Clear();
        _toolOutput.Clear();
    }
}
