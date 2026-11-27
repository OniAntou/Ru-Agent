using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

/// <summary>A later root of the session waits for a detached child that finished after its creator (ADR 0045).</summary>
public sealed class SubagentDetachedWaitTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_later_root_of_the_session_receives_a_detached_result_published_after_its_creator_ended(bool sameSession)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var creator = fixture.NewRun(1, 1, 1);
        await events.TryCreateRunAsync(creator, default);
        var child = fixture.NewRun(2, 2, 1);
        await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_one", creator.Run.RunId, "spawn_one", SubagentRole.Review, child, Detached: true), default);
        var now = HarnessStoreTestFixture.Timestamp.AddSeconds(1);
        Assert.Equal(HarnessCommitOutcome.Committed, (await events.CommitAsync(new(creator.Run.RunId, creator.Run.Version,
            Projection(creator.Run, HarnessRunState.Completed, 1, now),
            [new(HarnessStoreTestFixture.Event(5), HarnessEventKind.RunStateChanged, safeCode: "completed")], [], []), default)).Outcome);

        // The detached child finishes after its creator and its result is published.
        var summaryId = HarnessStoreTestFixture.Payload(20);
        var summary = Encoding.UTF8.GetBytes("Background review: two parser edge cases fail.");
        await events.CommitAsync(new(child.Run.RunId, child.Run.Version, Projection(child.Run, HarnessRunState.Completed, 1, now.AddSeconds(1)),
            [new(HarnessStoreTestFixture.Event(20), HarnessEventKind.ModelStepCompleted,
                modelStepId: new("step_" + new string('2', 32)), payloadId: summaryId)],
            [new(summaryId, HarnessPayloadKind.AssistantText, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Utf8Text, summary.Length, summary)], []), default);
        await new SqliteSubagentResultStore(fixture.Options, fixture.PayloadStore)
            .PublishTerminalAsync(creator.Run.RunId, "agent_one", child.Run.RunId, default);

        var later = sameSession ? fixture.NewRun(3, 1, 1, idempotencyHashCharacter: 'c') : fixture.NewRun(3, 3, 1);
        await events.TryCreateRunAsync(later, default);
        using var arguments = JsonDocument.Parse("{\"agent_ids\":[\"agent_one\"],\"timeout_ms\":30000}");
        var step = new ModelStepId("step_" + new string('1', 32));
        var call = new ToolCallId("call_" + new string('1', 32));
        var execution = new ExecutionId("exec_" + new string('1', 32));
        var payload = HarnessStoreTestFixture.Payload(10);
        var bytes = ToolArgumentsCodec.Serialize("provider_wait", "agent_wait", "1.0.0", arguments.RootElement);
        var proposed = Projection(later.Run, HarnessRunState.Running, 2, now.AddSeconds(2));
        await events.CommitAsync(new(later.Run.RunId, later.Run.Version, proposed,
            [new(HarnessStoreTestFixture.Event(10), HarnessEventKind.ToolProposed, modelStepId: step, toolCallId: call,
                providerCallId: "provider_wait", payloadId: payload),
             new(HarnessStoreTestFixture.Event(11), HarnessEventKind.ToolExecutionRequested, modelStepId: step,
                toolCallId: call, executionId: execution, providerCallId: "provider_wait")],
            [new(payload, HarnessPayloadKind.ToolArguments, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Json, bytes.Length, bytes)], []), default);
        var parked = Projection(proposed, HarnessRunState.WaitingSubagents, 1, now.AddSeconds(3));
        var park = new HarnessCommit(later.Run.RunId, proposed.Version, parked,
            [new(HarnessStoreTestFixture.Event(12), HarnessEventKind.RunStateChanged, modelStepId: step, safeCode: "waiting_subagents")], [], [],
            subagentWait: new(step, call, execution, "provider_wait", arguments.RootElement));

        if (!sameSession)
        {
            var refused = await Assert.ThrowsAsync<HarnessStoreException>(() => events.CommitAsync(park, default));
            Assert.Equal("subagent_not_found", refused.Code);
            return;
        }
        Assert.Equal(HarnessCommitOutcome.Committed, (await events.CommitAsync(park, default)).Outcome);
        var woken = await fixture.CreateEventStore().TryWakeSubagentWaitAsync(later.Run.RunId, now.AddSeconds(4), default);
        Assert.False(woken!.TimedOut);
        HarnessEvent? completion = null;
        await foreach (var item in events.ReadEventsAsync(later.Run.RunId, 0, default))
            if (item.Event.Kind == "tool.execution_completed") completion = item.Event;
        var content = await fixture.PayloadStore.ReadForModelAsync(later.Run.RunId, completion!.PayloadId!.Value, default);
        using var output = JsonDocument.Parse(content.Plaintext);
        var result = Assert.Single(output.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal("agent_one", result.GetProperty("agent_id").GetString());
        Assert.Equal("Background review: two parser edge cases fail.", result.GetProperty("summary").GetString());
    }

    private static AgentRun Projection(AgentRun run, HarnessRunState state, int count, DateTimeOffset now) =>
        new(run.RunId, run.SessionId, run.WorkspaceId, new(state), run.Version + 1, run.LastSequence + count,
            run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, run.Preset,
            run.PolicySnapshotVersion, run.PolicySnapshotSha256, run.CreatedAt, now, terminalAt: state.IsTerminal() ? now : null);
}
