using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence.Context;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class SubagentInboxRecoveryTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task Audit_only_recovery_step_does_not_break_inbox_or_hide_pending_native_dispatch(
        bool queued, bool unproposedCall, bool missingPayload)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var root = fixture.NewRun(1, 1, 1);
        await events.TryCreateRunAsync(root, default);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var child = fixture.NewRun(2, 2, 1);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, child), default);
        await fixture.PayloadStore.CaptureBaseContextAsync(child.Run.RunId, [], HarnessStoreTestFixture.Timestamp, default);
        if (queued) await agents.AppendMessageAsync(root.Run.RunId, "agent_one", "followup", "Inspect the sources", default);
        var run = (await events.GetRunAsync(child.Run.RunId, default))!;
        await events.CommitAsync(SubagentAdmission.CreateCommit(run, new("evt_" + Guid.NewGuid().ToString("N")), HarnessStoreTestFixture.Timestamp), default);

        async Task<PayloadId> CompleteStep(HarnessPayloadAudience audience, bool calls)
        {
            var active = (await events.GetRunAsync(child.Run.RunId, default))!;
            using var args = JsonDocument.Parse("{\"query\":\"fixture research\"}");
            var bytes = AssistantStepJsonCodec.Serialize(new AssistantStep("Suppressed fixture answer",
                calls ? [new("provider_search", "web_search", "1.0.0", args.RootElement)] : [],
                calls ? AssistantFinishReason.ToolCalls : AssistantFinishReason.Stop, 10));
            var payload = new PayloadId("payload_" + Guid.NewGuid().ToString("N"));
            var step = new ModelStepId("step_" + Guid.NewGuid().ToString("N"));
            var intents = new HarnessEventIntent[] {
                new(new("evt_" + Guid.NewGuid().ToString("N")), HarnessEventKind.ModelStepStarted, modelStepId: step),
                new(new("evt_" + Guid.NewGuid().ToString("N")), HarnessEventKind.ModelStepCompleted, modelStepId: step, payloadId: payload),
            };
            var next = new AgentRun(active.RunId, active.SessionId, active.WorkspaceId, active.Status,
                active.Version + 1, active.LastSequence + intents.Length, active.BudgetWindow,
                active.ModelStepsInWindow, active.ToolCallsInWindow, active.Preset, active.PolicySnapshotVersion,
                active.PolicySnapshotSha256, active.CreatedAt, active.UpdatedAt);
            await events.CommitAsync(new(active.RunId, active.Version, next, intents,
                [new(payload, HarnessPayloadKind.AssistantStep, audience, HarnessPayloadEncoding.Json, bytes.Length, bytes)], []), default);
            return payload;
        }

        await CompleteStep(HarnessPayloadAudience.ModelContext | HarnessPayloadAudience.AuthenticatedClient, unproposedCall);
        var audit = await CompleteStep(HarnessPayloadAudience.InternalOnly, false);
        var denied = await Assert.ThrowsAsync<HarnessStoreException>(() => fixture.PayloadStore.ReadForModelAsync(child.Run.RunId, audit, default));
        Assert.Equal("payload_model_forbidden", denied.Code);
        if (missingPayload)
        {
            await using var connection = fixture.CreateRawConnection();
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            // Simulate damaged storage, outside normal foreign-key guarded writes.
            command.CommandText = "PRAGMA foreign_keys=OFF; DELETE FROM agent_payloads WHERE payload_id=$payload";
            command.Parameters.AddWithValue("$payload", audit.Value);
            await command.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<HarnessStoreException>(() =>
                agents.DeliverMessagesAsync(child.Run.RunId, HarnessStoreTestFixture.Timestamp, default));
            Assert.Single(await agents.ReadPendingMessagesAsync(root.Run.RunId, "agent_one", default));
            return;
        }
        Assert.Equal(queued && !unproposedCall ? 1 : 0,
            await agents.DeliverMessagesAsync(child.Run.RunId, HarnessStoreTestFixture.Timestamp, default));
        Assert.Equal(unproposedCall && queued ? 1 : 0,
            (await agents.ReadPendingMessagesAsync(root.Run.RunId, "agent_one", default)).Count);
        var context = await new SqliteModelContextSource(fixture.Options, fixture.PayloadStore).LoadAsync(child.Run.RunId, default);
        Assert.Single(context.RunLocal, entry => entry.Kind == ModelContextEntryKind.AssistantStep);
        Assert.Equal(HarnessRunState.Running, (await events.GetRunAsync(child.Run.RunId, default))!.State);
    }
}
