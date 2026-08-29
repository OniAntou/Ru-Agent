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
        IReadOnlyList<ConversationOutboxIntent> conversationOutbox,
        ContextCompactionIntent? contextCompaction = null)
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
        if (contextCompaction is not null)
        {
            if (contextCompaction.RunId != runId)
            {
                throw new ArgumentException("Compaction run ID does not match the commit.", nameof(contextCompaction));
            }

            var summaryPayload = copiedPayloads.FirstOrDefault(
                payload => payload.PayloadId == contextCompaction.SummaryPayloadId);
            if (summaryPayload is null || summaryPayload.Kind != HarnessPayloadKind.ModelContextSummary)
            {
                throw new ArgumentException(
                    "Compaction must reference a summary payload in the same commit.",
                    nameof(contextCompaction));
            }
        }
        // Outbox intents may reference a payload created in this commit or one already
        // durable from an earlier commit (e.g. the user prompt created at run creation
        // and delivered after workspace trust approval). Existence is verified by the
        // store inside the write transaction.

        RunId = runId;
        ExpectedVersion = expectedVersion;
        NextRun = nextRun;
        Events = copiedEvents;
        Payloads = copiedPayloads;
        ConversationOutbox = copiedOutbox;
        ContextCompaction = contextCompaction;
    }

    public RunId RunId { get; }

    public long ExpectedVersion { get; }

    public AgentRun NextRun { get; }

    public IReadOnlyList<HarnessEventIntent> Events { get; }

    public IReadOnlyList<HarnessPayloadIntent> Payloads { get; }

    public IReadOnlyList<ConversationOutboxIntent> ConversationOutbox { get; }

    public ContextCompactionIntent? ContextCompaction { get; }

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
