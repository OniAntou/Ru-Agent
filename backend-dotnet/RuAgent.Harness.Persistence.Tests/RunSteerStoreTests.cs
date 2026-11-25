using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Steering;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class RunSteerStoreTests
{
    private static readonly string SteerOne = "steer_" + new string('1', 32);
    private static readonly string SteerTwo = "steer_" + new string('2', 32);

    [Fact]
    public async Task A_steer_waits_unbound_and_is_delivered_once_as_a_user_entry_at_the_boundary()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var (events, steers, runId) = await RunningAsync(fixture);

        Assert.Equal(RunSteerAppendOutcome.Accepted,
            await steers.AppendAsync(runId, SteerOne, "Dùng parser mới 🐈", HarnessStoreTestFixture.Timestamp, default));
        Assert.True(await steers.HasPendingAsync(runId, default));
        Assert.Equal(0L, await BoundUserEntriesAsync(fixture, runId));
        Assert.Equal(RunSteerContract.StatePending, Assert.Single(await steers.ListAsync(runId, default)).State);

        var before = (await events.GetRunAsync(runId, default))!;
        var delivered = Assert.Single(await steers.DeliverAsync(runId, HarnessStoreTestFixture.Timestamp, default));
        Assert.Equal(SteerOne, delivered.SteerId);
        Assert.Equal("Dùng parser mới 🐈", delivered.Text);
        Assert.Empty(await steers.DeliverAsync(runId, HarnessStoreTestFixture.Timestamp, default));
        Assert.False(await steers.HasPendingAsync(runId, default));

        var after = (await events.GetRunAsync(runId, default))!;
        Assert.Equal(before.LastSequence + 1, after.LastSequence);
        Assert.Equal(before.ModelStepsInWindow, after.ModelStepsInWindow);
        Assert.Equal(1L, await BoundUserEntriesAsync(fixture, runId));
        var note = Assert.Single(await ReadEventsAsync(events, runId), item => item.Kind == "run.note_added");
        Assert.Equal(RunSteerContract.DeliveredSafeCode, note.SafeCode);
        Assert.Equal(RunSteerContract.DeliveredPayloadId(SteerOne), note.PayloadId);
        var listed = Assert.Single(await steers.ListAsync(runId, default));
        Assert.Equal(RunSteerContract.StateDelivered, listed.State);
        // A read steer lives in the transcript; the list keeps only text a sender may resend.
        Assert.Equal(string.Empty, listed.Text);
    }

    [Fact]
    public async Task A_steers_images_wait_apart_from_the_runs_images_and_join_them_on_delivery()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var (_, steers, runId) = await RunningAsync(fixture);
        string[] images = [Png("a"), Png("b")];

        Assert.Equal(RunSteerAppendOutcome.Accepted,
            await steers.AppendAsync(runId, SteerOne, "look at these", images, HarnessStoreTestFixture.Timestamp, default));
        // Waiting images are not the run's images, so no model request carries them early.
        Assert.Empty(await fixture.PayloadStore.ListPayloadIdsAsync(runId, HarnessPayloadKind.UserImage, default));
        Assert.Equal(2, Assert.Single(await steers.ListAsync(runId, default)).ImageCount);

        var delivered = Assert.Single(await steers.DeliverAsync(runId, HarnessStoreTestFixture.Timestamp, default));
        Assert.Equal(images, delivered.ImageDataUrls);
        Assert.Equal(("look at these", 2), RuAgent.Protocol.Harness.AgentSteerText.Split(delivered.DeliveredText));
        var runImages = await fixture.PayloadStore.ListPayloadIdsAsync(runId, HarnessPayloadKind.UserImage, default);
        Assert.Equal(2, runImages.Count);

        // Each image is stored once: the waiting copy goes, and the run's image id derives
        // from it, so the steer's image rows still name what the run now holds.
        Assert.Empty(await fixture.PayloadStore.ListPayloadIdsAsync(runId, HarnessPayloadKind.SteerImage, default));
        var sources = await SteerImageSourcesAsync(fixture, SteerOne);
        Assert.Equal(sources.Select(RunSteerContract.DeliveredImagePayloadId), runImages);
    }

    [Fact]
    public async Task A_run_that_ends_drops_the_images_of_steers_it_never_read()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var (events, steers, runId) = await RunningAsync(fixture);
        await steers.AppendAsync(runId, SteerOne, "never read", [Png("a"), Png("b")], HarnessStoreTestFixture.Timestamp, default);
        Assert.Equal(2, (await fixture.PayloadStore.ListPayloadIdsAsync(runId, HarnessPayloadKind.SteerImage, default)).Count);

        var running = (await events.GetRunAsync(runId, default))!;
        var ended = new AgentRun(
            running.RunId, running.SessionId, running.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.Cancelled),
            running.Version + 1, running.LastSequence + 1,
            running.BudgetWindow, running.ModelStepsInWindow, running.ToolCallsInWindow,
            running.Preset, running.PolicySnapshotVersion, running.PolicySnapshotSha256,
            running.CreatedAt, running.UpdatedAt.AddSeconds(1), terminalAt: running.UpdatedAt.AddSeconds(1),
            terminalErrorCode: "cancelled");
        var result = await events.CommitAsync(new HarnessCommit(runId, running.Version, ended,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(901), HarnessEventKind.RunStateChanged)], [], []), default);

        Assert.Equal(HarnessCommitOutcome.Committed, result.Outcome);
        Assert.Empty(await fixture.PayloadStore.ListPayloadIdsAsync(runId, HarnessPayloadKind.SteerImage, default));
        // The returned steer still reads back with its text and image count.
        var steer = Assert.Single(await steers.ListAsync(runId, default));
        Assert.Equal(RunSteerContract.StateReturned, steer.State);
        Assert.Equal("never read", steer.Text);
        Assert.Equal(2, steer.ImageCount);
    }

    [Fact]
    public async Task Images_alone_can_steer_and_a_retry_with_other_images_conflicts()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var (_, steers, runId) = await RunningAsync(fixture);
        var now = HarnessStoreTestFixture.Timestamp;

        Assert.Equal(RunSteerAppendOutcome.Accepted, await steers.AppendAsync(runId, SteerOne, "", [Png("a")], now, default));
        Assert.Equal(RunSteerAppendOutcome.Duplicate, await steers.AppendAsync(runId, SteerOne, "", [Png("a")], now, default));
        Assert.Equal(RunSteerAppendOutcome.IdempotencyConflict, await steers.AppendAsync(runId, SteerOne, "", [Png("b")], now, default));
        var delivered = Assert.Single(await steers.DeliverAsync(runId, now, default));
        Assert.Equal((string.Empty, 1), RuAgent.Protocol.Harness.AgentSteerText.Split(delivered.DeliveredText));
    }

    [Fact]
    public async Task Waiting_steer_text_is_bounded_per_run_in_all()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var (_, steers, runId) = await RunningAsync(fixture);
        var large = new string('x', RunSteerContract.MaxCharacters);
        var fits = RunSteerContract.MaxPendingCharacters / RunSteerContract.MaxCharacters;
        for (var index = 0; index < fits; index++)
        {
            Assert.Equal(RunSteerAppendOutcome.Accepted, await steers.AppendAsync(runId,
                "steer_" + index.ToString("x32"), large, HarnessStoreTestFixture.Timestamp, default));
        }

        Assert.Equal(RunSteerAppendOutcome.QueueFull,
            await steers.AppendAsync(runId, SteerTwo, large, HarnessStoreTestFixture.Timestamp, default));
    }

    private static string Png(string seed) =>
        "data:image/png;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("png-" + seed));

    [Theory]
    [InlineData("model.step_started")]
    [InlineData("tool.proposed")]
    public async Task A_steer_never_lands_between_a_step_and_its_tool_results(string inFlight)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var (_, steers, runId) = await RunningAsync(fixture);
        await steers.AppendAsync(runId, SteerOne, "Stop after the parser", HarnessStoreTestFixture.Timestamp, default);
        await InsertRawEventAsync(fixture, runId, inFlight);

        Assert.Empty(await steers.DeliverAsync(runId, HarnessStoreTestFixture.Timestamp, default));
        Assert.True(await steers.HasPendingAsync(runId, default));
        Assert.Equal(0L, await BoundUserEntriesAsync(fixture, runId));
    }

    [Fact]
    public async Task A_retried_steer_answers_as_the_first_and_other_text_under_its_id_conflicts()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var (_, steers, runId) = await RunningAsync(fixture);
        var now = HarnessStoreTestFixture.Timestamp;

        Assert.Equal(RunSteerAppendOutcome.Accepted, await steers.AppendAsync(runId, SteerOne, "one", now, default));
        Assert.Equal(RunSteerAppendOutcome.Duplicate, await steers.AppendAsync(runId, SteerOne, "one", now, default));
        Assert.Equal(RunSteerAppendOutcome.IdempotencyConflict, await steers.AppendAsync(runId, SteerOne, "two", now, default));
        Assert.Single(await steers.ListAsync(runId, default));
    }

    [Fact]
    public async Task Pending_steers_are_bounded_per_run()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var (_, steers, runId) = await RunningAsync(fixture);
        for (var index = 0; index < RunSteerContract.MaxPending; index++)
        {
            Assert.Equal(RunSteerAppendOutcome.Accepted, await steers.AppendAsync(runId,
                "steer_" + index.ToString("x32"), "steer " + index, HarnessStoreTestFixture.Timestamp, default));
        }

        Assert.Equal(RunSteerAppendOutcome.QueueFull,
            await steers.AppendAsync(runId, SteerTwo, "one too many", HarnessStoreTestFixture.Timestamp, default));
    }

    [Theory]
    [InlineData(HarnessRunState.Created)]
    [InlineData(HarnessRunState.WaitingWorkspaceTrust)]
    public async Task A_run_that_will_not_step_again_refuses_a_steer(HarnessRunState state)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var (_, steers, runId) = await RunAsync(fixture, new HarnessRunStatus(state));

        Assert.Equal(RunSteerAppendOutcome.RunNotSteerable,
            await steers.AppendAsync(runId, SteerOne, "too late", HarnessStoreTestFixture.Timestamp, default));
        Assert.Equal(RunSteerAppendOutcome.RunNotFound,
            await steers.AppendAsync(HarnessStoreTestFixture.Run(9), SteerTwo, "nobody", HarnessStoreTestFixture.Timestamp, default));
    }

    [Fact]
    public async Task A_steer_still_pending_when_the_run_ends_reads_as_returned()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var (_, steers, runId) = await RunningAsync(fixture);
        await steers.AppendAsync(runId, SteerOne, "never delivered", HarnessStoreTestFixture.Timestamp, default);
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_runs SET state=$state,terminal_at_utc=$now,terminal_error_code='cancelled' WHERE run_id=$run";
            command.Parameters.AddWithValue("$state", (int)HarnessRunState.Cancelled);
            command.Parameters.AddWithValue("$now", HarnessStoreTestFixture.Timestamp.ToString("O"));
            command.Parameters.AddWithValue("$run", runId.Value);
            await command.ExecuteNonQueryAsync();
        }

        var steer = Assert.Single(await steers.ListAsync(runId, default));
        Assert.Equal(RunSteerContract.StateReturned, steer.State);
        Assert.Equal("never delivered", steer.Text);
        Assert.Empty(await steers.DeliverAsync(runId, HarnessStoreTestFixture.Timestamp, default));
        Assert.Equal(RunSteerAppendOutcome.RunNotSteerable,
            await steers.AppendAsync(runId, SteerTwo, "after the end", HarnessStoreTestFixture.Timestamp, default));
    }

    private static Task<(SqliteHarnessEventStore Events, SqliteRunSteerStore Steers, RunId RunId)> RunningAsync(
        HarnessStoreTestFixture fixture) =>
        RunAsync(fixture, new HarnessRunStatus(HarnessRunState.Running));

    private static async Task<(SqliteHarnessEventStore Events, SqliteRunSteerStore Steers, RunId RunId)> RunAsync(
        HarnessStoreTestFixture fixture, HarnessRunStatus status)
    {
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var run = fixture.NewRun(1, 1, 1, status: status);
        await events.TryCreateRunAsync(run, default);
        return (events, new SqliteRunSteerStore(fixture.Options, fixture.PayloadStore), run.Run.RunId);
    }

    private static async Task<IReadOnlyList<PayloadId>> SteerImageSourcesAsync(HarnessStoreTestFixture fixture, string steerId)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_id FROM agent_run_steer_images WHERE steer_id=$steer ORDER BY ordinal";
        command.Parameters.AddWithValue("$steer", steerId);
        var sources = new List<PayloadId>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) sources.Add(new PayloadId(reader.GetString(0)));
        return sources;
    }

    private static async Task<long> BoundUserEntriesAsync(HarnessStoreTestFixture fixture, RunId runId)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM agent_payloads p JOIN agent_run_steers s ON 1=1
            WHERE p.run_id=$run AND p.context_scope=1 AND p.context_role=1
              AND p.payload_id='payload_' || substr(s.steer_id, 7)
            """;
        command.Parameters.AddWithValue("$run", runId.Value);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task InsertRawEventAsync(HarnessStoreTestFixture fixture, RunId runId, string kind)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_events(run_id,sequence,event_id,kind,model_step_id,tool_call_id,occurred_at_utc)
            VALUES($run,(SELECT last_sequence+1 FROM agent_runs WHERE run_id=$run),$event,$kind,$step,$call,$now);
            UPDATE agent_runs SET last_sequence=last_sequence+1,version=version+1 WHERE run_id=$run;
            """;
        command.Parameters.AddWithValue("$run", runId.Value);
        command.Parameters.AddWithValue("$event", "evt_" + new string('e', 32));
        command.Parameters.AddWithValue("$step", "step_" + new string('e', 32));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$call", "call_" + new string('e', 32));
        command.Parameters.AddWithValue("$now", HarnessStoreTestFixture.Timestamp.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<HarnessEvent>> ReadEventsAsync(SqliteHarnessEventStore events, RunId runId)
    {
        var result = new List<HarnessEvent>();
        await foreach (var envelope in events.ReadEventsAsync(runId, 0, default)) result.Add(envelope.Event);
        return result;
    }
}
