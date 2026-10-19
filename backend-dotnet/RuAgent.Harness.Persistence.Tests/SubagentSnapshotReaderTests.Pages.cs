using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed partial class SubagentSnapshotReaderTests
{
    [Fact]
    public async Task Result_pages_preserve_Unicode_and_bind_root_agent_and_version_even_after_root_completion()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        var foreign = fixture.NewRun(3, 3, 1);
        var events = fixture.CreateEventStore();
        await events.TryCreateRunAsync(root, default);
        await events.TryCreateRunAsync(foreign, default);
        const string text = "a🙂ế\r\nXYZ🙂";
        var bytes = Encoding.UTF8.GetBytes(text);
        var payload = HarnessStoreTestFixture.Payload(2);
        var child = fixture.NewRun(2, 2, 1, payloads: [new(payload, HarnessPayloadKind.AssistantText,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext, HarnessPayloadEncoding.Utf8Text, bytes.Length, bytes)]);
        await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, child), default);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agent_runs SET state=$state,terminal_at_utc=$now WHERE run_id IN ($root,$run);
            UPDATE agent_events SET kind='model.step_completed',payload_id=$payload WHERE run_id=$run;
            """;
        command.Parameters.AddWithValue("$state", (int)HarnessRunState.Completed);
        command.Parameters.AddWithValue("$now", HarnessStoreTestFixture.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$root", root.Run.RunId.Value);
        command.Parameters.AddWithValue("$run", child.Run.RunId.Value);
        command.Parameters.AddWithValue("$payload", payload.Value);
        await command.ExecuteNonQueryAsync();
        await new SqliteSubagentResultStore(fixture.Options, fixture.PayloadStore).PublishTerminalAsync(root.Run.RunId, "agent_one", child.Run.RunId, default);
        var reader = new SqliteSubagentSnapshotReader(fixture.Options, fixture.PayloadStore);
        var joined = new StringBuilder();
        var offset = 0;
        while (true)
        {
            var page = await reader.ReadResultAsync(root.Run.RunId, "agent_one", 1, offset, 4, default);
            Assert.Equal(bytes.Length, page.TotalBytes);
            Assert.Equal(offset, page.Offset);
            joined.Append(page.Text);
            if (page.NextOffset is not { } next) break;
            Assert.True(next > offset);
            offset = next;
        }
        Assert.Equal(text, joined.ToString());
        var wrongRoot = await Assert.ThrowsAsync<HarnessStoreException>(() => reader.ReadResultAsync(foreign.Run.RunId, "agent_one", 1, 0, 4, default));
        Assert.Equal("subagent_not_found", wrongRoot.Code);
        var wrongVersion = await Assert.ThrowsAsync<HarnessStoreException>(() => reader.ReadResultAsync(root.Run.RunId, "agent_one", 2, 0, 4, default));
        Assert.Equal("subagent_result_not_found", wrongVersion.Code);
        var split = await Assert.ThrowsAsync<HarnessStoreException>(() => reader.ReadResultAsync(root.Run.RunId, "agent_one", 1, 2, 4, default));
        Assert.Equal("subagent_page_invalid", split.Code);
    }

    [Fact]
    public async Task Preparations_and_missing_relation_metadata_are_explicit_without_inventing_a_child()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, fixture.NewRun(2, 2, 1)), default);
        await new SqliteSubagentWorkspaceAllocationStore(fixture.Options, new(new FakeHarnessKeyStore()))
            .ReserveAsync(root.Run.RunId, "agent_preparing", default);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_subagents WHERE agent_id='agent_one'";
        await command.ExecuteNonQueryAsync();
        var snapshot = await new SqliteSubagentSnapshotReader(fixture.Options, fixture.PayloadStore).ReadAsync(root.Run.RunId, default);
        Assert.Empty(snapshot.Agents);
        Assert.Equal(1, snapshot.UnavailableAttempts);
        Assert.Equal("agent_preparing", Assert.Single(snapshot.Preparations).AgentId);
        Assert.Equal(SubagentWorkspaceAllocationStage.Reserved, Assert.Single(snapshot.Preparations).Stage);
    }
}
