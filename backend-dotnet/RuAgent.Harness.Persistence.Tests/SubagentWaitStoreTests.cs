using System.Text.Json;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class SubagentWaitStoreTests
{
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task Park_and_wait_binding_are_atomic_and_require_owned_authorized_call(bool owned, int summaryVariant)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1, status: new(HarnessRunState.Running));
        var events = fixture.CreateEventStore();
        await events.TryCreateRunAsync(root, default);
        await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_one", root.Run.RunId, "spawn_one", summaryVariant == 3 ? SubagentRole.Coding : SubagentRole.Review, fixture.NewRun(2, 2, 1)), default);
        using var arguments = JsonDocument.Parse(owned ? "{\"agent_ids\":[\"agent_one\"],\"timeout_ms\":30000}"
            : "{\"agent_ids\":[\"agent_other\"],\"timeout_ms\":30000}");
        var step = new ModelStepId("step_" + new string('1', 32));
        var call = new ToolCallId("call_" + new string('1', 32));
        var execution = new ExecutionId("exec_" + new string('1', 32));
        var payload = HarnessStoreTestFixture.Payload(10);
        var bytes = ToolArgumentsCodec.Serialize("provider_wait", "agent_wait", "1.0.0", arguments.RootElement);
        var now = HarnessStoreTestFixture.Timestamp.AddSeconds(1);
        var proposed = Projection(root.Run, HarnessRunState.Running, 2, now);
        await events.CommitAsync(new(root.Run.RunId, root.Run.Version, proposed,
            [new(HarnessStoreTestFixture.Event(10), HarnessEventKind.ToolProposed, modelStepId: step, toolCallId: call,
                providerCallId: "provider_wait", payloadId: payload),
             new(HarnessStoreTestFixture.Event(11), HarnessEventKind.ToolExecutionRequested, modelStepId: step,
                toolCallId: call, executionId: execution, providerCallId: "provider_wait")],
            [new(payload, HarnessPayloadKind.ToolArguments, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Json, bytes.Length, bytes)], []), default);
        var parked = Projection(proposed, HarnessRunState.WaitingSubagents, 1, now.AddSeconds(1));
        var commit = new HarnessCommit(root.Run.RunId, proposed.Version, parked,
            [new(HarnessStoreTestFixture.Event(12), HarnessEventKind.RunStateChanged, modelStepId: step, safeCode: "waiting_subagents")], [], [],
            subagentWait: new(step, call, execution, "provider_wait", arguments.RootElement));
        if (owned) Assert.Equal(HarnessCommitOutcome.Committed, (await events.CommitAsync(commit, default)).Outcome);
        else await Assert.ThrowsAsync<HarnessStoreException>(() => events.CommitAsync(commit, default));
        Assert.Equal(owned ? HarnessRunState.WaitingSubagents : HarnessRunState.Running,
            (await fixture.CreateEventStore().GetRunAsync(root.Run.RunId, default))!.State);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT deadline_at_utc FROM agent_subagent_waits WHERE root_run_id=$root";
        query.Parameters.AddWithValue("$root", root.Run.RunId.Value);
        var deadline = await query.ExecuteScalarAsync();
        if (owned) Assert.Equal(parked.UpdatedAt.AddSeconds(30).ToString("O"), deadline);
        else Assert.Null(deadline);
        if (owned)
        {
            var summaryText = summaryVariant == 2 ? string.Concat(Enumerable.Repeat("Kết quả kiểm tra 😀\n", 2000))
                : "Kết quả kiểm tra tiếng Việt 😀";
            if (summaryVariant != 0)
            {
                var child = fixture.NewRun(2, 2, 1).Run;
                var terminal = SubagentAdmission.CreateTerminalCommit(child, HarnessStoreTestFixture.Event(20), false,
                    "subagent_blocked", parked.UpdatedAt.AddSeconds(1));
                var summaryId = HarnessStoreTestFixture.Payload(20);
                var summaryBytes = Encoding.UTF8.GetBytes(summaryText);
                var endRun = summaryVariant == 3 ? Projection(child, HarnessRunState.Completed, 1, parked.UpdatedAt.AddSeconds(1)) : terminal.NextRun;
                await events.CommitAsync(new(child.RunId, child.Version, endRun,
                    [new(HarnessStoreTestFixture.Event(20), HarnessEventKind.ModelStepCompleted,
                        modelStepId: new("step_" + new string('2', 32)), payloadId: summaryId)],
                    [new(summaryId, HarnessPayloadKind.AssistantText, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                        HarnessPayloadEncoding.Utf8Text, summaryBytes.Length, summaryBytes)], []), default);
                var results = new SqliteSubagentResultStore(fixture.Options, fixture.PayloadStore);
                if (summaryVariant == 3)
                    await results.PublishFailureAsync(root.Run.RunId, "agent_one", child.RunId, "subagent_snapshot_changed", default);
                else await results.PublishTerminalAsync(root.Run.RunId, "agent_one", child.RunId, default);
                Assert.True(await fixture.CreateEventStore().TryWakeSubagentWaitAsync(root.Run.RunId, parked.UpdatedAt.AddSeconds(2), default));
            }
            else
            {
                Assert.False(await events.TryWakeSubagentWaitAsync(root.Run.RunId, parked.UpdatedAt.AddSeconds(29), default));
                Assert.True(await fixture.CreateEventStore().TryWakeSubagentWaitAsync(root.Run.RunId, parked.UpdatedAt.AddSeconds(30), default));
            }
            Assert.False(await events.TryWakeSubagentWaitAsync(root.Run.RunId, parked.UpdatedAt.AddSeconds(31), default));
            var resumed = await events.GetRunAsync(root.Run.RunId, default);
            Assert.Equal(HarnessRunState.Running, resumed!.State);
            Assert.Equal(parked.BudgetWindow, resumed.BudgetWindow);
            var completions = new List<HarnessEvent>();
            await foreach (var item in events.ReadEventsAsync(root.Run.RunId, 0, default))
                if (item.Event.Kind == "tool.execution_completed") completions.Add(item.Event);
            var completion = Assert.Single(completions);
            Assert.Equal(call, completion.ToolCallId);
            Assert.Equal(execution, completion.ExecutionId);
            Assert.Equal("provider_wait", completion.ProviderCallId);
            Assert.Equal(summaryVariant == 0 ? "subagent_wait_timeout" : "subagent_wait_completed", completion.SafeCode);
            var content = await fixture.PayloadStore.ReadForModelAsync(root.Run.RunId, completion.PayloadId!.Value, default);
            Assert.True(content.Plaintext.Length <= SubagentLimits.MaxInlineResultBytes);
            if (summaryVariant != 0)
            {
                using var output = JsonDocument.Parse(content.Plaintext);
                var item = output.RootElement.GetProperty("results")[0];
                Assert.Equal(summaryVariant == 2, item.GetProperty("summary_truncated").GetBoolean());
                if (summaryVariant == 3)
                {
                    Assert.Equal("failed", item.GetProperty("state").GetString());
                    Assert.Equal("subagent_snapshot_changed", item.GetProperty("code").GetString());
                    Assert.Contains("subagent_snapshot_changed", item.GetProperty("summary").GetString());
                }
                if (summaryVariant == 1) Assert.Equal(summaryText, item.GetProperty("summary").GetString());
                Assert.True(Assert.Single(await new SqliteSubagentResultStore(fixture.Options, fixture.PayloadStore)
                    .ListAsync(root.Run.RunId, "agent_one", default)).Delivered);
            }
        }
    }

    private static AgentRun Projection(AgentRun run, HarnessRunState state, int count, DateTimeOffset now) =>
        new(run.RunId, run.SessionId, run.WorkspaceId, new(state), run.Version + 1, run.LastSequence + count,
            run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, run.Preset,
            run.PolicySnapshotVersion, run.PolicySnapshotSha256, run.CreatedAt, now, terminalAt: state.IsTerminal() ? now : null);
}
