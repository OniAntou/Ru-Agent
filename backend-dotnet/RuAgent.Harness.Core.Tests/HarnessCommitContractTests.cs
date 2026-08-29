using System.Text;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class HarnessCommitContractTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Commit_defensively_copies_all_intent_lists_and_private_bytes()
    {
        var privateBytes = Encoding.UTF8.GetBytes("private assistant text");
        var payloads = new List<HarnessPayloadIntent>
        {
            Payload(privateBytes),
        };
        var events = new List<HarnessEventIntent>
        {
            Event("evt_11111111111111111111111111111111"),
        };
        var outbox = new List<ConversationOutboxIntent>
        {
            Outbox(
                "evt_22222222222222222222222222222222",
                payloads[0].PayloadId),
        };
        var commit = new HarnessCommit(
            RunId(),
            7,
            Run(version: 8, lastSequence: 12),
            events,
            payloads,
            outbox);

        privateBytes[0] = (byte)'X';
        events.Clear();
        payloads.Clear();
        outbox.Clear();

        Assert.Single(commit.Events);
        Assert.Single(commit.Payloads);
        Assert.Single(commit.ConversationOutbox);
        Assert.Equal(
            "private assistant text",
            Encoding.UTF8.GetString(commit.Payloads[0].Plaintext.Span));
        Assert.False(commit.Events is HarnessEventIntent[]);
        Assert.False(commit.Payloads is HarnessPayloadIntent[]);
        Assert.False(commit.ConversationOutbox is ConversationOutboxIntent[]);
    }

    [Fact]
    public void Commit_requires_compare_and_swap_version_run_binding_and_an_event()
    {
        var payload = Payload(Encoding.UTF8.GetBytes("private"));

        Assert.Throws<ArgumentException>(() => new HarnessCommit(
            RunId(),
            7,
            Run(version: 9, lastSequence: 12),
            new[] { Event("evt_11111111111111111111111111111111") },
            new[] { payload },
            Array.Empty<ConversationOutboxIntent>()));
        Assert.Throws<ArgumentException>(() => new HarnessCommit(
            RunId(),
            7,
            Run(
                version: 8,
                lastSequence: 12,
                runId: new RunId("run_22222222222222222222222222222222")),
            new[] { Event("evt_11111111111111111111111111111111") },
            new[] { payload },
            Array.Empty<ConversationOutboxIntent>()));
        Assert.Throws<ArgumentException>(() => new HarnessCommit(
            RunId(),
            7,
            Run(version: 8, lastSequence: 11),
            Array.Empty<HarnessEventIntent>(),
            Array.Empty<HarnessPayloadIntent>(),
            Array.Empty<ConversationOutboxIntent>()));
    }

    [Fact]
    public void Duplicate_intent_ids_fail_closed_including_event_outbox_collisions()
    {
        var duplicateEvent = Event("evt_11111111111111111111111111111111");
        var payload = Payload(Encoding.UTF8.GetBytes("private"));

        Assert.Throws<ArgumentException>(() => Commit(
            new[] { duplicateEvent, duplicateEvent },
            new[] { payload },
            Array.Empty<ConversationOutboxIntent>()));
        Assert.Throws<ArgumentException>(() => Commit(
            new[] { duplicateEvent },
            new[] { payload, payload },
            Array.Empty<ConversationOutboxIntent>()));
        Assert.Throws<ArgumentException>(() => Commit(
            new[] { duplicateEvent },
            new[] { payload },
            new[]
            {
                Outbox("evt_22222222222222222222222222222222", payload.PayloadId),
                Outbox("evt_22222222222222222222222222222222", payload.PayloadId),
            }));
        Assert.Throws<ArgumentException>(() => Commit(
            new[] { duplicateEvent },
            new[] { payload },
            new[] { Outbox(duplicateEvent.EventId.Value, payload.PayloadId) }));
    }

    [Fact]
    public void Payload_caps_audience_and_recovery_opaque_rules_are_enforced()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HarnessPayloadIntent(
            PayloadId(),
            HarnessPayloadKind.AssistantText,
            HarnessPayloadAudience.AuthenticatedClient,
            HarnessPayloadEncoding.Utf8Text,
            0,
            new byte[] { 1 }));
        Assert.Throws<ArgumentException>(() => new HarnessPayloadIntent(
            PayloadId(),
            HarnessPayloadKind.AssistantText,
            HarnessPayloadAudience.AuthenticatedClient,
            HarnessPayloadEncoding.Utf8Text,
            1,
            new byte[] { 1, 2 }));
        Assert.Throws<ArgumentException>(() => new HarnessPayloadIntent(
            PayloadId(),
            HarnessPayloadKind.RecoveryOpaque,
            HarnessPayloadAudience.AuthenticatedClient,
            HarnessPayloadEncoding.Opaque,
            8,
            new byte[] { 1, 2 }));
        Assert.Throws<ArgumentException>(() => new HarnessPayloadIntent(
            PayloadId(),
            HarnessPayloadKind.RecoveryOpaque,
            HarnessPayloadAudience.InternalOnly | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Opaque,
            8,
            new byte[] { 1, 2 }));
        Assert.Throws<ArgumentException>(() => new HarnessPayloadIntent(
            PayloadId(),
            HarnessPayloadKind.RecoveryOpaque,
            HarnessPayloadAudience.InternalOnly,
            HarnessPayloadEncoding.Utf8Text,
            8,
            new byte[] { 1, 2 }));

        var recovery = new HarnessPayloadIntent(
            PayloadId(),
            HarnessPayloadKind.RecoveryOpaque,
            HarnessPayloadAudience.InternalOnly,
            HarnessPayloadEncoding.Opaque,
            8,
            new byte[] { 0, 255, 1 });
        Assert.Equal(new byte[] { 0, 255, 1 }, recovery.Plaintext.ToArray());
    }

    [Fact]
    public void Context_summary_payload_is_model_only()
    {
        var bytes = Encoding.UTF8.GetBytes("compact summary");
        var payload = new HarnessPayloadIntent(
            PayloadId(),
            HarnessPayloadKind.ModelContextSummary,
            HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            bytes.Length,
            bytes);

        Assert.Equal(HarnessPayloadKind.ModelContextSummary, payload.Kind);
        Assert.Throws<ArgumentException>(() => new HarnessPayloadIntent(
            PayloadId(),
            HarnessPayloadKind.ModelContextSummary,
            HarnessPayloadAudience.AuthenticatedClient,
            HarnessPayloadEncoding.Utf8Text,
            bytes.Length,
            bytes));
    }

    [Fact]
    public void Compaction_intent_requires_ranges_hash_and_same_commit_payload()
    {
        var summaryPayload = new HarnessPayloadIntent(
            PayloadId(),
            HarnessPayloadKind.ModelContextSummary,
            HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            16,
            Encoding.UTF8.GetBytes("summary"));
        var valid = new ContextCompactionIntent(
            RunId(),
            "compact-11111111111111111111111111111111",
            summaryPayload.PayloadId,
            ContextCompactionStrategy.Normal,
            ContextCompactionReason.Automatic,
            [new ContextEntryRange("old", 1, 2)],
            200,
            100,
            new string('a', 64));

        Assert.Throws<ArgumentException>(() => new ContextCompactionIntent(
            RunId(),
            "compact-11111111111111111111111111111111",
            summaryPayload.PayloadId,
            ContextCompactionStrategy.Normal,
            ContextCompactionReason.Automatic,
            Array.Empty<ContextEntryRange>(),
            200,
            100,
            new string('a', 64)));
        Assert.Throws<ArgumentException>(() => new ContextCompactionIntent(
            RunId(),
            "compact-11111111111111111111111111111111",
            summaryPayload.PayloadId,
            ContextCompactionStrategy.Normal,
            ContextCompactionReason.Automatic,
            [new ContextEntryRange("old", 1, 2)],
            200,
            100,
            "ABC"));
        Assert.Throws<ArgumentException>(() => new HarnessCommit(
            RunId(),
            7,
            Run(version: 8, lastSequence: 12),
            new[] { Event("evt_11111111111111111111111111111111") },
            Array.Empty<HarnessPayloadIntent>(),
            Array.Empty<ConversationOutboxIntent>(),
            valid));
    }

    [Fact]
    public void Safe_event_has_only_typed_metadata_and_rejects_private_text()
    {
        var properties = typeof(HarnessEventIntent)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(properties, name =>
            name.Contains("Text", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Arguments", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Output", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Diff", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Bytes", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<ArgumentException>(() => new HarnessEventIntent(
            new EventId("evt_11111111111111111111111111111111"),
            HarnessEventKind.RunFailed,
            safeCode: "raw private text must not fit"));
    }

    [Fact]
    public void Outbox_may_reference_a_payload_from_an_earlier_commit()
    {
        // The trust-approval flow delivers the user prompt payload created at run
        // creation, so an outbox intent may point at a previously durable payload.
        // Existence of referenced payloads is enforced by the store inside the write
        // transaction ("outbox_payload_missing"), not by the commit contract.
        var missing = new PayloadId("payload_22222222222222222222222222222222");

        var commit = Commit(
            new[] { Event("evt_11111111111111111111111111111111") },
            new[] { Payload(Encoding.UTF8.GetBytes("private")) },
            new[] { Outbox("evt_22222222222222222222222222222222", missing) });

        Assert.Single(commit.ConversationOutbox);
    }

    private static HarnessCommit Commit(
        IReadOnlyList<HarnessEventIntent> events,
        IReadOnlyList<HarnessPayloadIntent> payloads,
        IReadOnlyList<ConversationOutboxIntent> outbox) =>
        new(RunId(), 7, Run(version: 8, lastSequence: 12), events, payloads, outbox);

    private static HarnessEventIntent Event(string eventId) =>
        new(new EventId(eventId), HarnessEventKind.RunStateChanged);

    private static HarnessPayloadIntent Payload(byte[] bytes) =>
        new(
            PayloadId(),
            HarnessPayloadKind.AssistantText,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            1024,
            bytes);

    private static ConversationOutboxIntent Outbox(string eventId, PayloadId payloadId) =>
        new(
            new EventId(eventId),
            new SessionId("11111111111111111111111111111111"),
            payloadId,
            ConversationOutboxRole.Assistant,
            HistoryWaitKind.FinalBeforeCompletion);

    private static AgentRun Run(long version, long lastSequence, RunId? runId = null) =>
        new(
            runId ?? RunId(),
            new SessionId("11111111111111111111111111111111"),
            new WorkspaceId("ws_11111111111111111111111111111111"),
            new HarnessRunStatus(HarnessRunState.Running),
            version,
            lastSequence,
            1,
            0,
            0,
            AgentPermissionPreset.Standard,
            1,
            new string('a', 64),
            Now,
            Now);

    private static RunId RunId() =>
        new("run_11111111111111111111111111111111");

    private static PayloadId PayloadId() =>
        new("payload_11111111111111111111111111111111");
}
