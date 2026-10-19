using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class SubagentUserApplyStoreTests
{
    [Fact]
    public async Task Queue_binds_exact_owned_successful_coding_result_and_never_replays_execution()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        var payload = HarnessStoreTestFixture.Payload(2);
        var child = fixture.NewRun(2, 2, 1, payloads: [new(payload, HarnessPayloadKind.AssistantText,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext, HarnessPayloadEncoding.Utf8Text, 1, new byte[] { 65 })]);
        var events = fixture.CreateEventStore();
        await events.TryCreateRunAsync(root, default);
        await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Coding, child), default);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var sql = connection.CreateCommand();
        sql.CommandText = "UPDATE agent_runs SET state=$running WHERE run_id=$root; UPDATE agent_runs SET state=$completed,terminal_at_utc=$now WHERE run_id=$child;";
        sql.Parameters.AddWithValue("$root", root.Run.RunId.Value);
        sql.Parameters.AddWithValue("$child", child.Run.RunId.Value);
        sql.Parameters.AddWithValue("$running", (int)HarnessRunState.Running);
        sql.Parameters.AddWithValue("$completed", (int)HarnessRunState.Completed);
        sql.Parameters.AddWithValue("$now", HarnessStoreTestFixture.Timestamp.ToString("O"));
        await sql.ExecuteNonQueryAsync();
        sql.CommandText = "UPDATE agent_events SET kind='model.step_completed',payload_id=$payload WHERE run_id=$child";
        sql.Parameters.AddWithValue("$payload", payload.Value);
        await sql.ExecuteNonQueryAsync();
        await new SqliteSubagentChangeSetStore(fixture.Options, new(new FakeHarnessKeyStore())).SaveAsync(child.Run.RunId,
            new("agent_one", root.Run.RunId.Value, fixture.RootPath, "snapshot_one", 1, []), default);
        await new SqliteSubagentResultStore(fixture.Options, fixture.PayloadStore).PublishTerminalAsync(root.Run.RunId, "agent_one", child.Run.RunId, default);
        var store = new SqliteSubagentUserApplyStore(fixture.Options);
        var first = await store.EnqueueAsync(root.Run.RunId, "agent_one", 1, default);
        Assert.Equal(first, await store.EnqueueAsync(root.Run.RunId, "agent_one", 1, default));
        Assert.Equal("queued", first.Status);
        Assert.Equal("agent_one", first.Proposal.Arguments.GetProperty("agent_id").GetString());
        Assert.StartsWith("user_apply_", first.Proposal.ProviderCallId);
        Assert.True(await store.UpdateAsync(first, "executing", "epoch_one", null, null, default));
        Assert.False(await store.UpdateAsync(first, "failed", "epoch_two", null, null, default));
        Assert.Equal("executing", Assert.Single(await store.ListAsync(root.Run.RunId, default)).Status);
        Assert.Equal("executing", (await store.EnqueueAsync(root.Run.RunId, "agent_one", 1, default)).Status);
        await Assert.ThrowsAsync<HarnessStoreException>(() => store.EnqueueAsync(root.Run.RunId, "other_agent", 1, default));
        await Assert.ThrowsAsync<HarnessStoreException>(() => store.EnqueueAsync(root.Run.RunId, "agent_one", 2, default));
        sql.CommandText = "UPDATE agent_runs SET state=$completed,terminal_at_utc=$now WHERE run_id=$root";
        await sql.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<HarnessStoreException>(() => store.EnqueueAsync(root.Run.RunId, "agent_one", 1, default));
    }
}
