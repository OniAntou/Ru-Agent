using System.Collections.ObjectModel;

namespace RuAgent.Harness.Core.Persistence;

public sealed record HarnessCommit
{
    public HarnessCommit(
        RunId runId,
        long expectedVersion,
        AgentRun nextRun,
        IReadOnlyList<HarnessEventIntent> events,
        IReadOnlyList<HarnessPayloadIntent> payloads,
        IReadOnlyList<ConversationOutboxIntent> conversationOutbox)
    {
        _ = runId.Value;
        if (expectedVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        ArgumentNullException.ThrowIfNull(nextRun);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(conversationOutbox);
        if (nextRun.RunId != runId)
        {
            throw new ArgumentException("Commit run ID does not match its projection.", nameof(nextRun));
        }

        if (nextRun.Version != expectedVersion + 1)
        {
            throw new ArgumentException("Commit must advance exactly one projection version.", nameof(nextRun));
        }

        var copiedEvents = Copy(events, nameof(events));
        var copiedPayloads = Copy(payloads, nameof(payloads));
        var copiedOutbox = Copy(conversationOutbox, nameof(conversationOutbox));
        if (copiedEvents.Count == 0)
        {
            throw new ArgumentException("A state commit requires at least one safe event.", nameof(events));
        }

        RejectDuplicateIds(copiedEvents, copiedPayloads, copiedOutbox);
        var committedPayloadIds = copiedPayloads
            .Select(payload => payload.PayloadId)
            .ToHashSet();
        if (copiedOutbox.Any(intent => !committedPayloadIds.Contains(intent.PayloadId)))
        {
            throw new ArgumentException(
                "Conversation outbox payload must be created in the same commit.",
                nameof(conversationOutbox));
        }

        RunId = runId;
        ExpectedVersion = expectedVersion;
        NextRun = nextRun;
        Events = copiedEvents;
        Payloads = copiedPayloads;
        ConversationOutbox = copiedOutbox;
    }

    public RunId RunId { get; }

    public long ExpectedVersion { get; }

    public AgentRun NextRun { get; }

    public IReadOnlyList<HarnessEventIntent> Events { get; }

    public IReadOnlyList<HarnessPayloadIntent> Payloads { get; }

    public IReadOnlyList<ConversationOutboxIntent> ConversationOutbox { get; }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source, string parameterName)
        where T : class
    {
        var copy = source.ToArray();
        if (copy.Any(item => item is null))
        {
            throw new ArgumentException("Intent collection cannot contain null.", parameterName);
        }

        return new ReadOnlyCollection<T>(copy);
    }

    private static void RejectDuplicateIds(
        IReadOnlyList<HarnessEventIntent> events,
        IReadOnlyList<HarnessPayloadIntent> payloads,
        IReadOnlyList<ConversationOutboxIntent> outbox)
    {
        var eventAndOutboxIds = events.Select(item => item.EventId)
            .Concat(outbox.Select(item => item.IntentId))
            .ToArray();
        if (eventAndOutboxIds.Distinct().Count() != eventAndOutboxIds.Length)
        {
            throw new ArgumentException("Duplicate event or outbox intent ID.");
        }

        if (payloads.Select(item => item.PayloadId).Distinct().Count() != payloads.Count)
        {
            throw new ArgumentException("Duplicate payload intent ID.");
        }
    }
}
