using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class SubagentResultStoreTests
{
    [Fact]
    public async Task Late_publication_keeps_result_version_bound_to_attempt_number()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var root = fixture.NewRun(1, 1, 1);
        var first = fixture.NewRun(2, 2, 1);
        await events.TryCreateRunAsync(root, default);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, first), default);
        await new SqliteAgentRunProviderSelectionStore(fixture.Options).PinAsync(first.Run.RunId,
            new("Custom OpenAI", "custom", "chat_completions", "https://example.invalid/v1/", "original", null, "{}", null), default);
        await events.CommitAsync(SubagentAdmission.CreateTerminalCommit(first.Run, HarnessStoreTestFixture.Event(10), false,
            "subagent_attempt_timeout", HarnessStoreTestFixture.Timestamp), default);
        await agents.AppendMessageAsync(root.Run.RunId, "agent_one", "followup_one", "Try again", default);
        var second = fixture.NewRun(3, 2, 1, idempotencyHashCharacter: 'd');
        await agents.CreateContinuationAsync(root.Run.RunId, "agent_one", first.Run.RunId, second, default);
        await events.CommitAsync(SubagentAdmission.CreateTerminalCommit(second.Run, HarnessStoreTestFixture.Event(11), false,
            "subagent_attempt_timeout", HarnessStoreTestFixture.Timestamp), default);
        var results = new SqliteSubagentResultStore(fixture.Options, fixture.PayloadStore);
        Assert.Equal(2, (await results.PublishTerminalAsync(root.Run.RunId, "agent_one", second.Run.RunId, default)).Version);
        Assert.Equal(1, (await results.PublishTerminalAsync(root.Run.RunId, "agent_one", first.Run.RunId, default)).Version);
    }

    [Theory]
    [InlineData(SubagentRole.Coding)]
    [InlineData(SubagentRole.Review)]
    public async Task Completed_coding_publication_requires_immutable_changes_but_review_does_not(SubagentRole role)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        var payload = HarnessStoreTestFixture.Payload(2);
        var text = System.Text.Encoding.UTF8.GetBytes("Final child result");
        var child = fixture.NewRun(2, 2, 1, payloads: [new(payload,
            RuAgent.Harness.Core.Persistence.HarnessPayloadKind.AssistantText,
            RuAgent.Harness.Core.Persistence.HarnessPayloadAudience.AuthenticatedClient | RuAgent.Harness.Core.Persistence.HarnessPayloadAudience.ModelContext,
            RuAgent.Harness.Core.Persistence.HarnessPayloadEncoding.Utf8Text, text.Length, text)]);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_one", root.Run.RunId, "spawn_one", role, child), default);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_runs SET state=$state,updated_at_utc=$now,terminal_at_utc=$now WHERE run_id=$run; UPDATE agent_events SET kind=$kind,payload_id=$payload WHERE run_id=$run";
        command.Parameters.AddWithValue("$state", (int)HarnessRunState.Completed);
        command.Parameters.AddWithValue("$now", "2026-10-18T00:00:00Z");
        command.Parameters.AddWithValue("$run", child.Run.RunId.Value);
        command.Parameters.AddWithValue("$payload", payload.Value);
        command.Parameters.AddWithValue("$kind", HarnessSqliteMapper.EventKind(RuAgent.Harness.Core.Persistence.HarnessEventKind.ModelStepCompleted));
        await command.ExecuteNonQueryAsync();
        var results = new SqliteSubagentResultStore(fixture.Options, fixture.PayloadStore);
        if (role == SubagentRole.Coding)
        {
            var error = await Assert.ThrowsAsync<HarnessStoreException>(() => results.PublishTerminalAsync(root.Run.RunId, "agent_one", child.Run.RunId, default));
            Assert.Equal("subagent_changes_missing", error.Code);
            await new SqliteSubagentChangeSetStore(fixture.Options, new WorkspaceJournalProtector(new FakeHarnessKeyStore()))
                .SaveAsync(child.Run.RunId, new("agent_one", root.Run.RunId.Value, fixture.RootPath, "snapshot_one", 1, []), default);
        }
        var result = await results.PublishTerminalAsync(root.Run.RunId, "agent_one", child.Run.RunId, default);
        Assert.Equal(1, result.Version);
        Assert.Equal(payload, result.PayloadId);
    }

    [Fact]
    public async Task Terminal_result_has_one_version_per_attempt_and_cannot_cross_roots()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        var child = fixture.NewRun(2, 2, 1);
        var events = fixture.CreateEventStore();
        await events.TryCreateRunAsync(root, default);
        await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, child), default);
        var results = new SqliteSubagentResultStore(fixture.Options, fixture.PayloadStore);
        await Assert.ThrowsAsync<HarnessStoreException>(() => results.PublishTerminalAsync(root.Run.RunId, "agent_one", child.Run.RunId, default));
        await events.CommitAsync(SubagentAdmission.CreateTerminalCommit(child.Run, HarnessStoreTestFixture.Event(10), false,
            "subagent_attempt_timeout", HarnessStoreTestFixture.Timestamp.AddMinutes(16)), default);
        var first = await results.PublishTerminalAsync(root.Run.RunId, "agent_one", child.Run.RunId, default);
        Assert.Equal(1, first.Version);
        Assert.Equal(HarnessRunState.Failed, first.State);
        Assert.Equal("subagent_attempt_timeout", first.Code);
        Assert.Equal(first, await new SqliteSubagentResultStore(fixture.Options, fixture.PayloadStore)
            .PublishTerminalAsync(root.Run.RunId, "agent_one", child.Run.RunId, default));
        await Assert.ThrowsAsync<HarnessStoreException>(() => results.PublishTerminalAsync(
            HarnessStoreTestFixture.Run(3), "agent_one", child.Run.RunId, default));
        Assert.Single(await results.ListAsync(root.Run.RunId, "agent_one", default));
    }
}
