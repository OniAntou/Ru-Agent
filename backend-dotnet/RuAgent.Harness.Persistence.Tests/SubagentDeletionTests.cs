using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class SubagentDeletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleting_parent_session_removes_owned_children_and_tombstones_internal_sessions(bool missingOwner)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var root = fixture.NewRun(1, 1, 1);
        var unrelated = fixture.NewRun(3, 3, 1);
        await events.TryCreateRunAsync(root, default);
        await events.TryCreateRunAsync(unrelated, default);
        var child = fixture.NewRun(2, 2, 1);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, child), default);
        await agents.AppendMessageAsync(root.Run.RunId, "agent_one", "message_one", "Follow up", default);
        if (missingOwner)
        {
            await using var orphanConnection = fixture.CreateRawConnection();
            await orphanConnection.OpenAsync();
            using var command = orphanConnection.CreateCommand();
            command.CommandText = "DELETE FROM agent_subagents WHERE agent_id='agent_one'";
            await command.ExecuteNonQueryAsync();
        }
        var artifacts = new HarnessSessionArtifactStore(fixture.Options);
        await artifacts.MarkSessionDeletedAsync(root.Run.SessionId.Value, HarnessStoreTestFixture.Timestamp, default);
        await artifacts.CloseAndDeleteAsync(root.Run.SessionId.Value, HarnessStoreTestFixture.Timestamp, default);
        Assert.Null(await events.GetRunAsync(child.Run.RunId, default));
        Assert.Null(await events.GetRunAsync(root.Run.RunId, default));
        Assert.NotNull(await events.GetRunAsync(unrelated.Run.RunId, default));
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT count(*) FROM agent_session_tombstones WHERE session_id=$session";
        query.Parameters.AddWithValue("$session", child.Run.SessionId.Value);
        Assert.Equal(1L, await query.ExecuteScalarAsync());
        query.CommandText = "SELECT count(*) FROM agent_session_settings WHERE session_id=$session";
        Assert.Equal(0L, await query.ExecuteScalarAsync());
        query.CommandText = "SELECT count(*) FROM agent_payloads WHERE run_id=$run";
        query.Parameters.AddWithValue("$run", child.Run.RunId.Value);
        Assert.Equal(0L, await query.ExecuteScalarAsync());
    }
}
