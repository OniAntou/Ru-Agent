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
    public async Task A_steer_a_terminal_run_never_read_joins_the_next_run_of_its_session()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var (events, steers, firstRun) = await RunningAsync(fixture);
        string[] images = [Png("a"), Png("b")];
        await steers.AppendAsync(firstRun, SteerOne, "never read", images, HarnessStoreTestFixture.Timestamp, default);
        await EndRunAsync(events, firstRun);

        // The images wait for the run that will read the steer; the end of the first run does
        // not drop them, because no client is expected to send the message again.
        Assert.Equal(2, (await fixture.PayloadStore.ListPayloadIdsAsync(firstRun, HarnessPayloadKind.SteerImage, default)).Count);

        var nextRun = await StartRunAsync(fixture, events, runNumber: 2, sessionNumber: 1);
        var delivered = Assert.Single(await steers.DeliverAsync(nextRun, HarnessStoreTestFixture.Timestamp, default));
        Assert.Equal(SteerOne, delivered.SteerId);
        Assert.Equal("never read", delivered.Text);
        Assert.Equal(images, delivered.ImageDataUrls);

        // It was delivered once for the session: the earlier run reports it delivered, the run
        // that read it is recorded, and the images moved there with the derived run image ids.
        Assert.Equal(RunSteerContract.StateDelivered, Assert.Single(await steers.ListAsync(firstRun, default)).State);
        Assert.Empty(await steers.DeliverAsync(nextRun, HarnessStoreTestFixture.Timestamp, default));
        Assert.Empty(await fixture.PayloadStore.ListPayloadIdsAsync(firstRun, HarnessPayloadKind.SteerImage, default));
        Assert.Equal(2, (await fixture.PayloadStore.ListPayloadIdsAsync(nextRun, HarnessPayloadKind.UserImage, default)).Count);
        Assert.Equal(nextRun.Value, await CarriedRunAsync(fixture, SteerOne));
    }

    [Fact]
    public async Task A_steer_of_another_session_and_one_a_running_run_still_holds_are_not_carried()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var (events, steers, firstRun) = await RunningAsync(fixture);
        await steers.AppendAsync(firstRun, SteerOne, "never read", HarnessStoreTestFixture.Timestamp, default);

        // The first run is still running, so its steer is pending for that run, not retained.
        var sameSession = await StartRunAsync(fixture, events, runNumber: 2, sessionNumber: 1);
        Assert.Empty(await steers.DeliverAsync(sameSession, HarnessStoreTestFixture.Timestamp, default));

        await EndRunAsync(events, firstRun);
        var otherSession = await StartRunAsync(fixture, events, runNumber: 3, sessionNumber: 2);
        Assert.Empty(await steers.DeliverAsync(otherSession, HarnessStoreTestFixture.Timestamp, default));

        // Only a later run of the same session reads it.
        var sameSessionNext = await StartRunAsync(fixture, events, runNumber: 4, sessionNumber: 1);
        Assert.Single(await steers.DeliverAsync(sameSessionNext, HarnessStoreTestFixture.Timestamp, default));
    }

    [Fact]
    public async Task A_session_lists_only_the_steers_its_runs_never_read()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var (events, steers, firstRun) = await RunningAsync(fixture);
        await steers.AppendAsync(firstRun, SteerOne, "never read", [Png("a")], HarnessStoreTestFixture.Timestamp, default);
        await EndRunAsync(events, firstRun);

        var returned = Assert.Single(await steers.ListReturnedForSessionAsync(HarnessStoreTestFixture.Session(1), default));
        Assert.Equal(SteerOne, returned.SteerId);
        Assert.Equal(firstRun.Value, returned.RunId);
        Assert.Equal("never read", returned.Text);
        Assert.Equal(1, returned.ImageCount);
        Assert.Empty(await steers.ListReturnedForSessionAsync(HarnessStoreTestFixture.Session(2), default));

        // The session's next run reads it, so a chat reopened after that lists nothing.
        var nextRun = await StartRunAsync(fixture, events, runNumber: 2, sessionNumber: 1);
        Assert.Single(await steers.DeliverAsync(nextRun, HarnessStoreTestFixture.Timestamp, default));
        Assert.Empty(await steers.ListReturnedForSessionAsync(HarnessStoreTestFixture.Session(1), default));
    }

    private static async Task<RunId> StartRunAsync(
        HarnessStoreTestFixture fixture, SqliteHarnessEventStore events, int runNumber, int sessionNumber)
    {
        // Each creation carries its own idempotency key: the fixture's default would replay
        // the first run of the session instead of creating another one.
        var next = fixture.NewRun(runNumber, sessionNumber, 1,
            idempotencyHashCharacter: (char)('a' + runNumber % 26),
            requestHashCharacter: (char)('b' + runNumber % 25),
            status: new HarnessRunStatus(HarnessRunState.Running));
        await events.TryCreateRunAsync(next, default);
        return next.Run.RunId;
    }

    private static async Task EndRunAsync(SqliteHarnessEventStore events, RunId runId)
    {
        var running = (await events.GetRunAsync(runId, default))!;
        var ended = new AgentRun(
            running.RunId, running.SessionId, running.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.Cancelled),
            running.Version + 1, running.LastSequence + 1,
            running.BudgetWindow, running.ModelStepsInWindow, running.ToolCallsInWindow,
            running.Preset, running.PolicySnapshotVersion, running.PolicySnapshotSha256,
            running.CreatedAt, running.UpdatedAt.AddSeconds(1), terminalAt: running.UpdatedAt.AddSeconds(1),
            terminalErrorCode: "cancelled");
        Assert.Equal(HarnessCommitOutcome.Committed, (await events.CommitAsync(new HarnessCommit(runId, running.Version, ended,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(901), HarnessEventKind.RunStateChanged)], [], []), default)).Outcome);
    }

    private static async Task<string?> CarriedRunAsync(HarnessStoreTestFixture fixture, string steerId)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id FROM agent_run_steer_carries WHERE steer_id=$steer";
        command.Parameters.AddWithValue("$steer", steerId);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (string)value;
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

    [Fact]
    public async Task A_steer_still_pending_when_the_final_reply_is_committed_reads_as_returned()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var (_, steers, runId) = await RunningAsync(fixture);
        await steers.AppendAsync(runId, SteerOne, "never delivered", HarnessStoreTestFixture.Timestamp, default);
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_runs SET state=$state,history_wait_kind=$kind WHERE run_id=$run";
            command.Parameters.AddWithValue("$state", (int)HarnessRunState.WaitingHistory);
            command.Parameters.AddWithValue("$kind", (int)HistoryWaitKind.FinalBeforeCompletion);
            command.Parameters.AddWithValue("$run", runId.Value);
            await command.ExecuteNonQueryAsync();
        }

        // The run will not reach another model step, so the sender must offer it as the next turn.
        var steer = Assert.Single(await steers.ListAsync(runId, default));
        Assert.Equal(RunSteerContract.StateReturned, steer.State);
        Assert.Equal("never delivered", steer.Text);
        Assert.Equal(RunSteerContract.StateReturned, await steers.FindStateAsync(runId, SteerOne, default));
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
