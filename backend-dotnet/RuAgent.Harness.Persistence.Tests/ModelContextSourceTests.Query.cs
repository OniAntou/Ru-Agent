using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Context;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed partial class ModelContextSourceTests
{
    [Fact]
    public async Task Context_event_join_uses_event_id_index_at_scale()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var request = fixture.NewRun(1, 1, 1);
        var eventStore = fixture.CreateEventStore();
        await eventStore.TryCreateRunAsync(request, CancellationToken.None);

        const int unrelatedEventCount = 1_600;
        var unrelatedEvents = Enumerable.Range(0, unrelatedEventCount)
            .Select(index => new HarnessEventIntent(
                HarnessStoreTestFixture.Event(1_000 + index),
                HarnessEventKind.RunNoteAdded,
                safeCode: "plan_fixture"))
            .ToArray();
        await eventStore.CommitAsync(
            new HarnessCommit(
                request.Run.RunId,
                0,
                NextRun(request.Run, version: 1, lastSequence: unrelatedEventCount + 1),
                unrelatedEvents,
                [],
                []),
            CancellationToken.None);

        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {SqliteModelContextSource.ContextEntriesSql}";
        command.Parameters.AddWithValue("$run_id", request.Run.RunId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(3));
        }

        Assert.Contains(plan, line => line.Contains("agent_events", StringComparison.Ordinal)
            && line.Contains("event_id=?", StringComparison.Ordinal));
        Assert.Contains("e.run_id = p.run_id", SqliteModelContextSource.ContextEntriesSql, StringComparison.Ordinal);
        Assert.Contains("p.context_scope = 1", SqliteModelContextSource.ContextEntriesSql, StringComparison.Ordinal);
        Assert.Contains("substr(p.context_source_id, 1, 6) = 'event:'", SqliteModelContextSource.ContextEntriesSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Context_event_join_does_not_attach_a_receipt_from_another_run()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var eventStore = fixture.CreateEventStore();
        var first = fixture.NewRun(1, 1, 1);
        var second = fixture.NewRun(2, 2, 1);
        await eventStore.TryCreateRunAsync(first, CancellationToken.None);
        await eventStore.TryCreateRunAsync(second, CancellationToken.None);
        await fixture.PayloadStore.CaptureBaseContextAsync(first.Run.RunId, [], HarnessStoreTestFixture.Timestamp, CancellationToken.None);
        await fixture.PayloadStore.CaptureBaseContextAsync(second.Run.RunId, [], HarnessStoreTestFixture.Timestamp, CancellationToken.None);

        var firstPayload = TextPayload(30, HarnessPayloadKind.ToolOutput, "first result");
        var secondPayload = TextPayload(31, HarnessPayloadKind.ToolOutput, "second result");
        await CommitToolResultAsync(eventStore, first.Run, firstPayload, 30);
        await CommitToolResultAsync(eventStore, second.Run, secondPayload, 31);

        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_payloads SET context_source_id = $source WHERE payload_id = $payload";
            command.Parameters.AddWithValue("$source", $"event:{HarnessStoreTestFixture.Event(31).Value}");
            command.Parameters.AddWithValue("$payload", firstPayload.PayloadId.Value);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var error = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            new SqliteModelContextSource(fixture.Options, fixture.PayloadStore)
                .LoadAsync(first.Run.RunId, CancellationToken.None));

        Assert.Equal("context_payload_invalid", error.Code);
    }

    private static async Task CommitToolResultAsync(
        SqliteHarnessEventStore eventStore,
        AgentRun current,
        HarnessPayloadIntent payload,
        int number)
    {
        var outcome = await eventStore.CommitAsync(
            new HarnessCommit(
                current.RunId,
                expectedVersion: 0,
                NextRun(current, version: 1, lastSequence: 2),
                [new HarnessEventIntent(
                    HarnessStoreTestFixture.Event(number),
                    HarnessEventKind.ToolExecutionCompleted,
                    toolCallId: new ToolCallId($"call_{number:x32}"),
                    providerCallId: $"provider_{number}",
                    executionId: new ExecutionId($"exec_{number:x32}"),
                    payloadId: payload.PayloadId,
                    safeCode: "ok",
                    toolOutcome: ToolExecutionOutcome.Succeeded,
                    sideEffectsPossible: false)],
                [payload],
                []),
            CancellationToken.None);
        Assert.Equal(HarnessCommitOutcome.Committed, outcome.Outcome);
    }
}
