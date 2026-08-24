namespace RuAgent.Harness.Core;

public sealed record HarnessEvent
{
    public HarnessEvent(
        EventId eventId,
        RunId runId,
        long sequence,
        string kind,
        DateTimeOffset occurredAt,
        PayloadId? payloadId = null)
    {
        _ = eventId.Value;
        _ = runId.Value;
        if (payloadId is { } privatePayloadId)
        {
            _ = privatePayloadId.Value;
        }

        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (!IsSafeKind(kind))
        {
            throw new ArgumentException(
                "Event kind must be a bounded lowercase token.",
                nameof(kind));
        }

        EventId = eventId;
        RunId = runId;
        Sequence = sequence;
        Kind = kind;
        OccurredAt = occurredAt;
        PayloadId = payloadId;
    }

    public EventId EventId { get; }

    public RunId RunId { get; }

    public long Sequence { get; }

    public string Kind { get; }

    public DateTimeOffset OccurredAt { get; }

    public PayloadId? PayloadId { get; }

    private static bool IsSafeKind(string? value)
    {
        if (value is not { Length: >= 1 and <= 64 }
            || value[0] is not (>= 'a' and <= 'z'))
        {
            return false;
        }

        return value.All(character =>
            character is (>= 'a' and <= 'z')
                or (>= '0' and <= '9')
                or '_'
                or '.');
    }
}
