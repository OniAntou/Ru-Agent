using System.Text;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Context;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class HarnessProjectionTransactionTests
{
    [Fact]
    public async Task Context_compaction_projects_a_summary_and_hides_covered_entries_after_restart()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var eventStore = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await eventStore.TryCreateRunAsync(request, CancellationToken.None);
        await fixture.PayloadStore.CaptureBaseContextAsync(
            request.Run.RunId,
            [new HarnessBaseContextEntry(
                HarnessStoreTestFixture.Payload(190),
                "base:system",
                1,
                ModelContextRole.System,
                "system rules")],
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);

        var coveredPayload = UserPayload(191, "old context that should be summarized");
        var runWithContext = HarnessEventStoreTests.CopyRun(
            request.Run,
            new HarnessRunStatus(HarnessRunState.Running),
            version: 1,
            lastSequence: 2);
        await eventStore.CommitAsync(
            new HarnessCommit(
                request.Run.RunId,
                0,
                runWithContext,
                [new HarnessEventIntent(
                    HarnessStoreTestFixture.Event(191),
                    HarnessEventKind.ModelStepCompleted,
                    payloadId: coveredPayload.PayloadId)],
                [coveredPayload],
                []),
            CancellationToken.None);

        var summaryText = "[context_compaction id=compact-1 untrusted=true]\nold context retained\n[/context_compaction]";
        var summaryBytes = Encoding.UTF8.GetBytes(summaryText);
        var summaryPayload = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(192),
            HarnessPayloadKind.ModelContextSummary,
            HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            4096,
            summaryBytes);
        var compactedRun = HarnessEventStoreTests.CopyRun(
            runWithContext,
            new HarnessRunStatus(HarnessRunState.Running),
            version: 2,
            lastSequence: 3);
        var coveredRange = new ContextEntryRange(
            $"event:{HarnessStoreTestFixture.Event(191).Value}",
            2,
            2);
        var compacted = await eventStore.CommitAsync(
            new HarnessCommit(
                request.Run.RunId,
                1,
                compactedRun,
                [new HarnessEventIntent(
                    HarnessStoreTestFixture.Event(192),
                    HarnessEventKind.ContextCompacted,
                    safeCode: "context_compacted",
                    safeCount: 1)],
                [summaryPayload],
                [],
                new ContextCompactionIntent(
                    request.Run.RunId,
                    "compact-1",
                    summaryPayload.PayloadId,
                    ContextCompactionStrategy.Normal,
                    ContextCompactionReason.Manual,
                    [coveredRange],
                    beforeTokens: 120,
                    afterTokens: 40,
                    inputHash: HarnessStoreTestFixture.Sha('d'))),
            CancellationToken.None);

        Assert.Equal(HarnessCommitOutcome.Committed, compacted.Outcome);
        var first = await new AgentContextAssembler(
                new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.Run.RunId, CancellationToken.None);
        var afterRestart = await new AgentContextAssembler(
                new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.Run.RunId, CancellationToken.None);

        Assert.DoesNotContain(first.Entries, entry => entry.SourceId == $"event:{HarnessStoreTestFixture.Event(191).Value}");
        var summary = Assert.Single(first.Entries, entry => entry.Kind == ModelContextEntryKind.Summary);
        Assert.Equal("compact-1", summary.SourceId);
        Assert.Equal(summaryText, summary.Content);
        Assert.Equal([coveredRange], summary.SummaryRanges);
        Assert.Equal(first.CanonicalUtf8.ToArray(), afterRestart.CanonicalUtf8.ToArray());
        Assert.Equal(1L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_context_compactions"));
        var events = new List<HarnessEventEnvelope>();
        await foreach (var envelope in eventStore.ReadEventsAsync(
                           request.Run.RunId,
                           0,
                           CancellationToken.None))
        {
            events.Add(envelope);
        }

        Assert.Contains(events, item => item.Event.Kind == "context.compacted");
        Assert.All(events.Where(item => item.Event.Kind == "context.compacted"),
            item => Assert.Null(item.Event.PayloadId));
    }

    [Fact]
    public async Task Failed_compaction_projection_rolls_back_summary_and_compaction_row()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var eventStore = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await eventStore.TryCreateRunAsync(request, CancellationToken.None);
        var current = HarnessEventStoreTests.CopyRun(
            request.Run,
            new HarnessRunStatus(HarnessRunState.Running),
            version: 1,
            lastSequence: 2);
        await eventStore.CommitAsync(
            new HarnessCommit(
                request.Run.RunId,
                0,
                current,
                [new HarnessEventIntent(
                    HarnessStoreTestFixture.Event(193),
                    HarnessEventKind.RunStateChanged)],
                [],
                []),
            CancellationToken.None);

        var summaryPayload = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(194),
            HarnessPayloadKind.ModelContextSummary,
            HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            1024,
            Encoding.UTF8.GetBytes("summary"));
        var next = HarnessEventStoreTests.CopyRun(
            current,
            new HarnessRunStatus(HarnessRunState.Running),
            version: 2,
            lastSequence: 3);
        var failing = new HarnessCommit(
            request.Run.RunId,
            1,
            next,
            [new HarnessEventIntent(
                HarnessStoreTestFixture.Event(193),
                HarnessEventKind.ContextCompacted)],
            [summaryPayload],
            [],
            new ContextCompactionIntent(
                request.Run.RunId,
                "compact-rollback",
                summaryPayload.PayloadId,
                ContextCompactionStrategy.Normal,
                ContextCompactionReason.Automatic,
                [new ContextEntryRange("event:missing", 2, 2)],
                beforeTokens: 100,
                afterTokens: 50,
                inputHash: HarnessStoreTestFixture.Sha('e')));

        var exception = await Assert.ThrowsAsync<HarnessStoreException>(
            () => eventStore.CommitAsync(failing, CancellationToken.None));

        Assert.Equal("harness_commit_failed", exception.Code);
        Assert.Equal(1, (await eventStore.GetRunAsync(request.Run.RunId, CancellationToken.None))!.Version);
        Assert.Equal(0L, await ScalarAsync(
            fixture,
            $"SELECT COUNT(*) FROM agent_payloads WHERE payload_id = '{summaryPayload.PayloadId.Value}'"));
        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_context_compactions"));
    }

    [Fact]
    public async Task Event_projection_payload_and_outbox_commit_atomically()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await store.TryCreateRunAsync(request, CancellationToken.None);
        var payload = UserPayload(100, "visible user message");
        var next = HarnessEventStoreTests.CopyRun(
            request.Run,
            new HarnessRunStatus(HarnessRunState.WaitingHistory, HistoryWaitKind.UserBeforeFirstStep),
            version: 1,
            lastSequence: 2);
        var commit = new HarnessCommit(
            request.Run.RunId,
            expectedVersion: 0,
            next,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(100), HarnessEventKind.RunStateChanged, payloadId: payload.PayloadId)],
            [payload],
            [new ConversationOutboxIntent(
                HarnessStoreTestFixture.Event(101),
                request.Run.SessionId,
                payload.PayloadId,
                ConversationOutboxRole.User,
                HistoryWaitKind.UserBeforeFirstStep)]);

        var result = await store.CommitAsync(commit, CancellationToken.None);

        Assert.Equal(HarnessCommitOutcome.Committed, result.Outcome);
        Assert.Equal(1L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_payloads"));
        Assert.Equal(1L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_conversation_outbox"));
        Assert.Equal(2L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_events"));
        Assert.Equal(1L, await ScalarAsync(fixture, "SELECT version FROM agent_runs"));
    }

    [Fact]
    public async Task Prompt_receipt_and_composed_event_commit_atomically_as_internal_data()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await store.TryCreateRunAsync(request, CancellationToken.None);

        var bytes = Encoding.UTF8.GetBytes(
            "{\"composer_version\":\"1.0.0\",\"prompt_sha256\":\"" +
            new string('0', 64) + "\",\"block_digests\":[]}");
        var payload = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(101),
            HarnessPayloadKind.PromptReceipt,
            HarnessPayloadAudience.InternalOnly,
            HarnessPayloadEncoding.Json,
            16 * 1024,
            bytes);
        var next = HarnessEventStoreTests.CopyRun(
            request.Run,
            new HarnessRunStatus(HarnessRunState.Running),
            version: 1,
            lastSequence: 3);
        var composed = new HarnessEventIntent(
            HarnessStoreTestFixture.Event(101),
            HarnessEventKind.ModelPromptComposed,
            modelStepId: new ModelStepId("step_" + new string('1', 32)),
            payloadId: payload.PayloadId,
            safeCode: "composed");
        var completion = new HarnessEventIntent(
            HarnessStoreTestFixture.Event(102),
            HarnessEventKind.ModelStepCompleted,
            modelStepId: composed.ModelStepId);

        var result = await store.CommitAsync(
            new HarnessCommit(
                request.Run.RunId,
                expectedVersion: 0,
                next,
                [composed, completion],
                [payload],
                []),
            CancellationToken.None);

        Assert.Equal(HarnessCommitOutcome.Committed, result.Outcome);
        Assert.Equal(1L, await ScalarAsync(
            fixture,
            "SELECT COUNT(*) FROM agent_payloads WHERE kind = " +
            (int)HarnessPayloadKind.PromptReceipt));
        Assert.Equal(1L, await ScalarAsync(
            fixture,
            "SELECT COUNT(*) FROM agent_events WHERE kind = 'model.prompt_composed'"));
        Assert.Equal(0L, await ScalarAsync(
            fixture,
            "SELECT COUNT(*) FROM agent_payloads WHERE kind = " +
            (int)HarnessPayloadKind.PromptReceipt + " AND context_scope IS NOT NULL"));
        var events = new List<HarnessEventEnvelope>();
        await foreach (var envelope in store.ReadEventsAsync(
                           request.Run.RunId,
                           0,
                           CancellationToken.None))
        {
            events.Add(envelope);
        }
        Assert.Contains(events, item => item.Event.Kind == "model.prompt_composed");
        var clientError = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            fixture.PayloadStore.ReadForClientAsync(
                request.Run.RunId,
                payload.PayloadId,
                CancellationToken.None));
        Assert.Equal("payload_client_forbidden", clientError.Code);
    }

    [Fact]
    public async Task Failed_event_insert_rolls_back_projection_payload_and_outbox()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await store.TryCreateRunAsync(request, CancellationToken.None);
        var first = HarnessEventStoreTests.CopyRun(
            request.Run,
            new HarnessRunStatus(HarnessRunState.WaitingWorkspaceTrust),
            version: 1,
            lastSequence: 2);
        var duplicateEventId = HarnessStoreTestFixture.Event(110);
        await store.CommitAsync(
            new HarnessCommit(
                request.Run.RunId,
                0,
                first,
                [new HarnessEventIntent(duplicateEventId, HarnessEventKind.RunStateChanged)],
                [],
                []),
            CancellationToken.None);
        var payload = UserPayload(111, "must roll back");
        var second = HarnessEventStoreTests.CopyRun(
            first,
            new HarnessRunStatus(HarnessRunState.WaitingHistory, HistoryWaitKind.UserBeforeFirstStep),
            version: 2,
            lastSequence: 3);
        var failing = new HarnessCommit(
            request.Run.RunId,
            1,
            second,
            [new HarnessEventIntent(duplicateEventId, HarnessEventKind.RunStateChanged)],
            [payload],
            [new ConversationOutboxIntent(
                HarnessStoreTestFixture.Event(112),
                request.Run.SessionId,
                payload.PayloadId,
                ConversationOutboxRole.User,
                HistoryWaitKind.UserBeforeFirstStep)]);

        var exception = await Assert.ThrowsAsync<HarnessStoreException>(
            () => store.CommitAsync(failing, CancellationToken.None));
        var loaded = await store.GetRunAsync(request.Run.RunId, CancellationToken.None);

        Assert.Equal("harness_commit_failed", exception.Code);
        Assert.Equal(1, loaded!.Version);
        Assert.Equal(0L, await ScalarAsync(
            fixture,
            $"SELECT COUNT(*) FROM agent_payloads WHERE payload_id = '{payload.PayloadId.Value}'"));
        Assert.Equal(0L, await ScalarAsync(
            fixture,
            $"SELECT COUNT(*) FROM agent_conversation_outbox WHERE intent_id = '{HarnessStoreTestFixture.Event(112).Value}'"));
    }

    [Fact]
    public async Task Compare_and_swap_conflict_writes_nothing()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await store.TryCreateRunAsync(request, CancellationToken.None);
        var first = HarnessEventStoreTests.CopyRun(
            request.Run,
            new HarnessRunStatus(HarnessRunState.Running),
            version: 1,
            lastSequence: 2);
        var stale = HarnessEventStoreTests.CopyRun(
            request.Run,
            new HarnessRunStatus(HarnessRunState.Cancelled),
            version: 1,
            lastSequence: 2,
            terminalAt: request.Run.UpdatedAt.AddSeconds(1));
        await store.CommitAsync(
            new HarnessCommit(
                request.Run.RunId,
                0,
                first,
                [new HarnessEventIntent(HarnessStoreTestFixture.Event(120), HarnessEventKind.RunStateChanged)],
                [],
                []),
            CancellationToken.None);

        var result = await store.CommitAsync(
            new HarnessCommit(
                request.Run.RunId,
                0,
                stale,
                [new HarnessEventIntent(HarnessStoreTestFixture.Event(121), HarnessEventKind.RunStateChanged)],
                [],
                []),
            CancellationToken.None);

        Assert.Equal(HarnessCommitOutcome.VersionConflict, result.Outcome);
        Assert.Equal(1, result.Run!.Version);
        Assert.Equal(0L, await ScalarAsync(
            fixture,
            $"SELECT COUNT(*) FROM agent_events WHERE event_id = '{HarnessStoreTestFixture.Event(121).Value}'"));
    }

    [Fact]
    public async Task Schema_rejects_duplicate_run_sequence()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await store.TryCreateRunAsync(request, CancellationToken.None);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_events(event_id, run_id, sequence, kind, occurred_at_utc)
            VALUES ($event_id, $run_id, 1, 'run.state_changed', $occurred_at)
            """;
        command.Parameters.AddWithValue("$event_id", HarnessStoreTestFixture.Event(130).Value);
        command.Parameters.AddWithValue("$run_id", request.Run.RunId.Value);
        command.Parameters.AddWithValue("$occurred_at", HarnessStoreTestFixture.Timestamp.ToString("O"));

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Failed_initial_event_insert_rolls_back_the_implicit_session_selection()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        await fixture.RegisterWorkspaceAsync(2);
        var store = fixture.CreateEventStore();
        var first = fixture.NewRun(1, 1, 1, 'a', 'b');
        await store.TryCreateRunAsync(first, CancellationToken.None);
        var secondBase = fixture.NewRun(2, 2, 2, 'c', 'd');
        var second = new NewAgentRun(
            secondBase.Run,
            [new HarnessEventIntent(first.Events[0].EventId, HarnessEventKind.RunStateChanged)],
            secondBase.Payloads,
            secondBase.ConversationOutbox,
            secondBase.CreationIdentity);

        await Assert.ThrowsAsync<HarnessStoreException>(
            () => store.TryCreateRunAsync(second, CancellationToken.None));

        Assert.Equal(0L, await ScalarAsync(
            fixture,
            $"SELECT COUNT(*) FROM agent_session_settings WHERE session_id = '{second.Run.SessionId.Value}'"));
        Assert.Equal(0L, await ScalarAsync(
            fixture,
            $"SELECT COUNT(*) FROM agent_runs WHERE run_id = '{second.Run.RunId.Value}'"));
    }

    [Fact]
    public async Task Outbox_cannot_target_a_different_session_than_its_run()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await store.TryCreateRunAsync(request, CancellationToken.None);
        var payload = UserPayload(140, "wrong session");
        var next = HarnessEventStoreTests.CopyRun(
            request.Run,
            new HarnessRunStatus(HarnessRunState.WaitingHistory, HistoryWaitKind.UserBeforeFirstStep),
            version: 1,
            lastSequence: 2);
        var commit = new HarnessCommit(
            request.Run.RunId,
            0,
            next,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(140), HarnessEventKind.RunStateChanged, payloadId: payload.PayloadId)],
            [payload],
            [new ConversationOutboxIntent(
                HarnessStoreTestFixture.Event(141),
                HarnessStoreTestFixture.Session(2),
                payload.PayloadId,
                ConversationOutboxRole.User,
                HistoryWaitKind.UserBeforeFirstStep)]);

        var exception = await Assert.ThrowsAsync<HarnessStoreException>(
            () => store.CommitAsync(commit, CancellationToken.None));

        Assert.Equal("outbox_session_mismatch", exception.Code);
        Assert.Equal(0L, await ScalarAsync(
            fixture,
            $"SELECT COUNT(*) FROM agent_payloads WHERE payload_id = '{payload.PayloadId.Value}'"));
    }

    [Fact]
    public async Task Outbox_is_rejected_outside_its_atomic_history_boundary()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await store.TryCreateRunAsync(request, CancellationToken.None);
        var payload = UserPayload(145, "not waiting for history");
        var next = HarnessEventStoreTests.CopyRun(
            request.Run,
            new HarnessRunStatus(HarnessRunState.Running),
            version: 1,
            lastSequence: 2);
        var commit = new HarnessCommit(
            request.Run.RunId,
            0,
            next,
            [new HarnessEventIntent(
                HarnessStoreTestFixture.Event(145),
                HarnessEventKind.RunStateChanged,
                payloadId: payload.PayloadId)],
            [payload],
            [new ConversationOutboxIntent(
                HarnessStoreTestFixture.Event(146),
                request.Run.SessionId,
                payload.PayloadId,
                ConversationOutboxRole.User,
                HistoryWaitKind.UserBeforeFirstStep)]);

        var exception = await Assert.ThrowsAsync<HarnessStoreException>(
            () => store.CommitAsync(commit, CancellationToken.None));

        Assert.Equal("outbox_history_boundary_invalid", exception.Code);
        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_payloads"));
    }

    [Fact]
    public async Task User_history_outbox_requires_a_user_prompt_payload()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await store.TryCreateRunAsync(request, CancellationToken.None);
        var bytes = Encoding.UTF8.GetBytes("wrong visible kind");
        var payload = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(147),
            HarnessPayloadKind.AssistantText,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            1024,
            bytes);
        var next = HarnessEventStoreTests.CopyRun(
            request.Run,
            new HarnessRunStatus(
                HarnessRunState.WaitingHistory,
                HistoryWaitKind.UserBeforeFirstStep),
            version: 1,
            lastSequence: 2);
        var commit = new HarnessCommit(
            request.Run.RunId,
            0,
            next,
            [new HarnessEventIntent(
                HarnessStoreTestFixture.Event(147),
                HarnessEventKind.RunStateChanged,
                payloadId: payload.PayloadId)],
            [payload],
            [new ConversationOutboxIntent(
                HarnessStoreTestFixture.Event(148),
                request.Run.SessionId,
                payload.PayloadId,
                ConversationOutboxRole.User,
                HistoryWaitKind.UserBeforeFirstStep)]);

        var exception = await Assert.ThrowsAsync<HarnessStoreException>(
            () => store.CommitAsync(commit, CancellationToken.None));

        Assert.Equal("outbox_payload_kind_invalid", exception.Code);
    }

    [Fact]
    public async Task Deleting_a_run_cascades_events_payloads_and_outbox()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var payload = UserPayload(150, "delete with run");
        var requestBase = fixture.NewRun(1, 1, 1);
        var waitingRun = HarnessEventStoreTests.CopyRun(
            requestBase.Run,
            new HarnessRunStatus(
                HarnessRunState.WaitingHistory,
                HistoryWaitKind.UserBeforeFirstStep),
            version: 0,
            lastSequence: 1);
        var request = new NewAgentRun(
            waitingRun,
            [new HarnessEventIntent(
                requestBase.Events[0].EventId,
                HarnessEventKind.RunStateChanged,
                payloadId: payload.PayloadId)],
            [payload],
            [new ConversationOutboxIntent(
                HarnessStoreTestFixture.Event(151),
                requestBase.Run.SessionId,
                payload.PayloadId,
                ConversationOutboxRole.User,
                HistoryWaitKind.UserBeforeFirstStep)],
            requestBase.CreationIdentity);
        await store.TryCreateRunAsync(request, CancellationToken.None);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            DELETE FROM agent_runs WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", request.Run.RunId.Value);

        await command.ExecuteNonQueryAsync();

        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_events"));
        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_payloads"));
        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_conversation_outbox"));
    }

    private static HarnessPayloadIntent UserPayload(int number, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(number),
            HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            1024,
            bytes);
    }

    private static async Task<long> ScalarAsync(
        HarnessStoreTestFixture fixture,
        string commandText)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
}
