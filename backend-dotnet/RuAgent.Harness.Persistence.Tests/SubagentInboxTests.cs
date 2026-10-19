using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class SubagentInboxTests
{
    [Theory]
    [InlineData("canceled")]
    [InlineData("model.step_started")]
    [InlineData("tool.proposed")]
    [InlineData("native_unproposed")]
    public async Task Cancellation_or_incomplete_dispatch_keeps_messages_undelivered(string boundary)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var root = fixture.NewRun(1, 1, 1);
        await events.TryCreateRunAsync(root, default);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var child = fixture.NewRun(2, 2, 1);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, child), default);
        await agents.AppendMessageAsync(root.Run.RunId, "agent_one", "message_one", "Follow up", default);
        var created = (await events.GetRunAsync(child.Run.RunId, default))!;
        await events.CommitAsync(SubagentAdmission.CreateCommit(created,
            new("evt_" + new string('f', 32)), HarnessStoreTestFixture.Timestamp), default);
        if (boundary == "canceled") await agents.CancelFamilyAsync(root.Run.RunId, default);
        else if (boundary == "native_unproposed")
        {
            var run = (await events.GetRunAsync(child.Run.RunId, default))!;
            var next = new AgentRun(run.RunId, run.SessionId, run.WorkspaceId, run.Status, run.Version + 1,
                run.LastSequence + 1, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow,
                run.Preset, run.PolicySnapshotVersion, run.PolicySnapshotSha256, run.CreatedAt, run.UpdatedAt);
            using var arguments = System.Text.Json.JsonDocument.Parse("{\"path\":\"x.txt\"}");
            var bytes = AssistantStepJsonCodec.Serialize(new AssistantStep("read", [new("provider_read", "read", "1.0.0", arguments.RootElement)],
                AssistantFinishReason.ToolCalls, 16));
            var payload = new PayloadId("payload_" + new string('e', 32));
            await events.CommitAsync(new(run.RunId, run.Version, next,
                [new(new("evt_" + new string('e', 32)), HarnessEventKind.ModelStepCompleted,
                    modelStepId: new("step_" + new string('e', 32)), payloadId: payload)],
                [new(payload, HarnessPayloadKind.AssistantStep, HarnessPayloadAudience.ModelContext | HarnessPayloadAudience.AuthenticatedClient,
                    HarnessPayloadEncoding.Json, bytes.Length, bytes)], []), default);
        }
        else
        {
            await using var connection = fixture.CreateRawConnection();
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO agent_events(run_id,sequence,event_id,kind,model_step_id,tool_call_id,occurred_at_utc)
                VALUES($run,3,$event,$kind,$step,$call,$now);
                UPDATE agent_runs SET last_sequence=3,version=version+1 WHERE run_id=$run;
                """;
            command.Parameters.AddWithValue("$run", child.Run.RunId.Value);
            command.Parameters.AddWithValue("$event", "evt_" + new string('e', 32));
            command.Parameters.AddWithValue("$step", "step_" + new string('e', 32));
            command.Parameters.AddWithValue("$kind", boundary);
            command.Parameters.AddWithValue("$call", "call_" + new string('e', 32));
            command.Parameters.AddWithValue("$now", HarnessStoreTestFixture.Timestamp.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(0, await agents.DeliverMessagesAsync(child.Run.RunId, HarnessStoreTestFixture.Timestamp, default));
        Assert.Single(await agents.ReadPendingMessagesAsync(root.Run.RunId, "agent_one", default));
    }

    [Fact]
    public async Task Inbox_delivers_once_at_running_boundary_without_resetting_budget_or_creating_outbox()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var root = fixture.NewRun(1, 1, 1);
        await events.TryCreateRunAsync(root, default);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var child = fixture.NewRun(2, 2, 1);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, child), default);
        await agents.AppendMessageAsync(root.Run.RunId, "agent_one", "message_one", "Kiểm tra thêm parser 🐈", default);
        Assert.Equal(0, await agents.DeliverMessagesAsync(child.Run.RunId, HarnessStoreTestFixture.Timestamp, default));
        var created = (await events.GetRunAsync(child.Run.RunId, default))!;
        await events.CommitAsync(SubagentAdmission.CreateCommit(created,
            new("evt_" + new string('f', 32)), HarnessStoreTestFixture.Timestamp), default);
        var before = (await events.GetRunAsync(child.Run.RunId, default))!;
        Assert.Equal(1, await agents.DeliverMessagesAsync(child.Run.RunId, HarnessStoreTestFixture.Timestamp, default));
        Assert.Equal(0, await agents.DeliverMessagesAsync(child.Run.RunId, HarnessStoreTestFixture.Timestamp, default));
        Assert.Empty(await agents.ReadPendingMessagesAsync(root.Run.RunId, "agent_one", default));
        var after = (await events.GetRunAsync(child.Run.RunId, default))!;
        Assert.Equal(before.BudgetWindow, after.BudgetWindow);
        Assert.Equal(before.ModelStepsInWindow, after.ModelStepsInWindow);
        Assert.Equal(before.ToolCallsInWindow, after.ToolCallsInWindow);
        Assert.Equal(before.LastSequence + 1, after.LastSequence);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM agent_payloads WHERE run_id=$run AND context_scope=1 AND context_role=1";
        command.Parameters.AddWithValue("$run", child.Run.RunId.Value);
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }
}
