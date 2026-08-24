namespace RuAgent.Harness.Core;

public readonly record struct HarnessRunStatus
{
    public HarnessRunStatus(HarnessRunState state, HistoryWaitKind? historyWaitKind = null)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (historyWaitKind is { } kind && !Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(historyWaitKind));
        }

        if (state == HarnessRunState.WaitingHistory && historyWaitKind is null)
        {
            throw new ArgumentException(
                "WaitingHistory requires a history wait kind.",
                nameof(historyWaitKind));
        }

        if (state != HarnessRunState.WaitingHistory && historyWaitKind is not null)
        {
            throw new ArgumentException(
                "A history wait kind is valid only for WaitingHistory.",
                nameof(historyWaitKind));
        }

        State = state;
        HistoryWaitKind = historyWaitKind;
    }

    public HarnessRunState State { get; }
    public HistoryWaitKind? HistoryWaitKind { get; }
}
