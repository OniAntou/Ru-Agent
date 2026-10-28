using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class SubagentResultOutcomeTests
{
    [Theory]
    [InlineData(SubagentRole.Review)]
    [InlineData(SubagentRole.Coding)]
    public async Task Version_23_backfills_result_outcomes_and_marks_missing_coding_artifacts_failed(SubagentRole role)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var root = fixture.NewRun(1, 1, 1);
        var child = fixture.NewRun(2, 2, 1);
        await events.TryCreateRunAsync(root, default);
        await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_one", root.Run.RunId, "spawn_one", role, child), default);
        var results = new SqliteSubagentResultStore(fixture.Options, fixture.PayloadStore);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        if (role == SubagentRole.Coding)
        {
            command.CommandText = "UPDATE agent_runs SET state=7,terminal_at_utc=updated_at_utc WHERE run_id=$run";
            command.Parameters.AddWithValue("$run", child.Run.RunId.Value);
            await command.ExecuteNonQueryAsync();
            await results.PublishFailureAsync(root.Run.RunId, "agent_one", child.Run.RunId, "subagent_snapshot_changed", default);
        }
        else
        {
            await events.CommitAsync(SubagentAdmission.CreateTerminalCommit(child.Run, HarnessStoreTestFixture.Event(5),
                false, "subagent_attempt_timeout", HarnessStoreTestFixture.Timestamp), default);
            await results.PublishTerminalAsync(root.Run.RunId, "agent_one", child.Run.RunId, default);
        }
        await fixture.ResetSessionHistorySchemaToV29Async();
        command.CommandText = "DROP TABLE agent_subagent_result_outcomes; PRAGMA user_version=23";
        await command.ExecuteNonQueryAsync();
        Assert.True((await HarnessSqliteSchema.InitializeAsync(fixture.Options, default)).IsAvailable);
        var result = Assert.Single(await results.ListAsync(root.Run.RunId, "agent_one", default));
        Assert.Equal(HarnessRunState.Failed, result.State);
        Assert.Equal(role == SubagentRole.Coding ? "subagent_changes_missing" : "subagent_attempt_timeout", result.Code);
        Assert.Equal(result, await results.PublishTerminalAsync(root.Run.RunId, "agent_one", child.Run.RunId, default));
    }

    [Fact]
    public async Task Failed_collection_is_durable_and_immutable_without_rewriting_completed_execution()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var root = fixture.NewRun(1, 1, 1);
        var child = fixture.NewRun(2, 2, 1);
        await events.TryCreateRunAsync(root, default);
        await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Coding, child), default);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_runs SET state=$completed,terminal_at_utc=updated_at_utc WHERE run_id=$run";
        command.Parameters.AddWithValue("$completed", (int)HarnessRunState.Completed);
        command.Parameters.AddWithValue("$run", child.Run.RunId.Value);
        await command.ExecuteNonQueryAsync();
        var results = new SqliteSubagentResultStore(fixture.Options, fixture.PayloadStore);
        var failed = await results.PublishFailureAsync(root.Run.RunId, "agent_one", child.Run.RunId, "subagent_snapshot_changed", default);
        Assert.Equal(HarnessRunState.Failed, failed.State);
        Assert.Equal("subagent_snapshot_changed", failed.Code);
        Assert.Equal(HarnessRunState.Completed, (await events.GetRunAsync(child.Run.RunId, default))!.State);
        var restarted = new SqliteSubagentResultStore(fixture.Options, fixture.PayloadStore);
        Assert.Equal(failed, await restarted.PublishTerminalAsync(root.Run.RunId, "agent_one", child.Run.RunId, default));
        Assert.Equal(failed, Assert.Single(await restarted.ListAsync(root.Run.RunId, "agent_one", default)));
        Assert.Empty(await restarted.ListUnpublishedAsync(null, default));
        await Assert.ThrowsAsync<HarnessStoreException>(() => restarted.PublishFailureAsync(root.Run.RunId,
            "agent_one", child.Run.RunId, "subagent_scope_missing", default));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Corrupt_result_metadata_is_rejected_on_replay_and_list(bool replay, bool missingOutcome)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var root = fixture.NewRun(1, 1, 1);
        var child = fixture.NewRun(2, 2, 1);
        await events.TryCreateRunAsync(root, default);
        await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, child), default);
        await events.CommitAsync(SubagentAdmission.CreateTerminalCommit(child.Run, HarnessStoreTestFixture.Event(5),
            false, "subagent_attempt_timeout", HarnessStoreTestFixture.Timestamp), default);
        var results = new SqliteSubagentResultStore(fixture.Options, fixture.PayloadStore);
        await results.PublishTerminalAsync(root.Run.RunId, "agent_one", child.Run.RunId, default);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = missingOutcome ? "DELETE FROM agent_subagent_result_outcomes" : "UPDATE agent_subagent_results SET result_version=2";
        await command.ExecuteNonQueryAsync();
        var error = replay
            ? await Assert.ThrowsAsync<HarnessStoreException>(() => results.PublishTerminalAsync(root.Run.RunId, "agent_one", child.Run.RunId, default))
            : await Assert.ThrowsAsync<HarnessStoreException>(() => results.ListAsync(root.Run.RunId, "agent_one", default));
        Assert.Equal(missingOutcome ? "subagent_result_outcome_missing" : "subagent_result_version_invalid", error.Code);
    }
}
