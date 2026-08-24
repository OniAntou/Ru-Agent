namespace RuAgent.Harness.Core.Persistence;

public enum ConversationOutboxRole
{
    User,
    Assistant,
}

public sealed record ConversationOutboxIntent
{
    public ConversationOutboxIntent(
        EventId intentId,
        SessionId sessionId,
        PayloadId payloadId,
        ConversationOutboxRole role,
        HistoryWaitKind historyWaitKind)
    {
        _ = intentId.Value;
        _ = sessionId.Value;
        _ = payloadId.Value;
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        if (!Enum.IsDefined(historyWaitKind))
        {
            throw new ArgumentOutOfRangeException(nameof(historyWaitKind));
        }

        if ((role == ConversationOutboxRole.User
                && historyWaitKind != HistoryWaitKind.UserBeforeFirstStep)
            || (role == ConversationOutboxRole.Assistant
                && historyWaitKind != HistoryWaitKind.FinalBeforeCompletion))
        {
            throw new ArgumentException(
                "Outbox role must match its history acknowledgement kind.",
                nameof(historyWaitKind));
        }

        IntentId = intentId;
        SessionId = sessionId;
        PayloadId = payloadId;
        Role = role;
        HistoryWaitKind = historyWaitKind;
    }

    public EventId IntentId { get; }

    public SessionId SessionId { get; }

    public PayloadId PayloadId { get; }

    public ConversationOutboxRole Role { get; }

    public HistoryWaitKind HistoryWaitKind { get; }
}
