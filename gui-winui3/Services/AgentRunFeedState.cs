namespace RuApp.Services;

/// <summary>State owned by one controller's transient and terminal feed handling.</summary>
internal sealed class AgentRunFeedState
{
    private readonly AgentRunDeltaDeduplicator _deduplicator = new();
    private int _droppedAtIngress;
    private int _droppedAtUiGate;
    private int _droppedAsDuplicates;
    private int _droppedAfterCancel;

    internal string? TerminalStatus { get; set; }

    internal AgentTransientDropReport Drops => new(
        Volatile.Read(ref _droppedAtIngress),
        Volatile.Read(ref _droppedAtUiGate),
        Volatile.Read(ref _droppedAsDuplicates),
        Volatile.Read(ref _droppedAfterCancel));

    internal void ResetForRun()
    {
        _deduplicator.Reset();
        TerminalStatus = null;
    }

    internal bool TryAcceptAssistant(string runId, string? stepId, long sequence) =>
        _deduplicator.TryAcceptAssistant(runId, stepId, sequence);

    internal bool TryAcceptToolOutput(string runId, string? executionId, long sequence) =>
        _deduplicator.TryAcceptToolOutput(runId, executionId, sequence);

    internal void RecordIngressDrop() => Interlocked.Increment(ref _droppedAtIngress);
    internal void RecordUiGateDrop() => Interlocked.Increment(ref _droppedAtUiGate);
    internal void RecordDuplicateDrop() => Interlocked.Increment(ref _droppedAsDuplicates);
    internal void RecordCancelDrop() => Interlocked.Increment(ref _droppedAfterCancel);
}
