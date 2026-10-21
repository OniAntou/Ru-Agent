using System.Text;
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

public sealed class SubagentContinuationContextTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Continuation_inherits_closed_child_history_with_exact_native_call_relationships(bool complete)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var root = fixture.NewRun(1, 1, 1, payloads: [new(new("payload_" + new string('d', 32)),
            HarnessPayloadKind.UserPrompt, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, 100, Encoding.UTF8.GetBytes("Parent private transcript"))]);
        await events.TryCreateRunAsync(root, default);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var seed = fixture.NewRun(2, 2, 1);
        var original = seed.Run;
        var run = new AgentRun(original.RunId, original.SessionId, original.WorkspaceId, original.Status, 0, complete ? 3 : 2, 1, 0, 0,
            original.Preset, original.PolicySnapshotVersion, original.PolicySnapshotSha256, original.CreatedAt, original.UpdatedAt);
        using var arguments = JsonDocument.Parse("{\"path\":\"src/x.txt\"}");
        var step = new ModelStepId("step_" + new string('a', 32));
        var assistant = AssistantStepJsonCodec.Serialize(new AssistantStep("inspect", [new("provider_read", "read", "1.0.0", arguments.RootElement)],
            AssistantFinishReason.ToolCalls, 16));
        var promptId = new PayloadId("payload_" + new string('a', 32));
        var assistantId = new PayloadId("payload_" + new string('b', 32));
        var outputId = new PayloadId("payload_" + new string('c', 32));
        var audience = HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext;
        var first = new NewAgentRun(run,
            [new(new("evt_" + new string('a', 32)), HarnessEventKind.RunStateChanged, payloadId: promptId),
             new(new("evt_" + new string('b', 32)), HarnessEventKind.ModelStepCompleted, modelStepId: step, payloadId: assistantId),
             new(new("evt_" + new string('c', 32)), HarnessEventKind.ToolExecutionCompleted, modelStepId: step,
                 toolCallId: new("call_" + new string('a', 32)), executionId: new("exec_" + new string('a', 32)),
                 payloadId: outputId, providerCallId: "provider_read", safeCode: "ok",
                 toolOutcome: ToolExecutionOutcome.Succeeded, sideEffectsPossible: false)],
            [new(promptId, HarnessPayloadKind.UserPrompt, audience, HarnessPayloadEncoding.Utf8Text, 100, Encoding.UTF8.GetBytes("Child task 🐈")),
             new(assistantId, HarnessPayloadKind.AssistantStep, audience, HarnessPayloadEncoding.Json, assistant.Length, assistant),
             new(outputId, HarnessPayloadKind.ToolOutput, audience, HarnessPayloadEncoding.Utf8Text, 100, Encoding.UTF8.GetBytes("Nội dung tệp"))],
            [], seed.CreationIdentity);
        if (!complete) first = new(run, first.Events.Take(2).ToArray(), first.Payloads.Take(2).ToArray(), [], seed.CreationIdentity);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, first), default);
        await new SqliteAgentRunProviderSelectionStore(fixture.Options).PinAsync(run.RunId,
            new("Custom OpenAI", "custom", "chat_completions", "https://example.invalid/v1/", "fixture", null, "{}", null), default);
        await fixture.PayloadStore.CaptureBaseContextAsync(run.RunId, [], HarnessStoreTestFixture.Timestamp, default);
        await events.CommitAsync(SubagentAdmission.CreateTerminalCommit(run, new("evt_" + new string('f', 32)),
            false, "fixture_failure", HarnessStoreTestFixture.Timestamp), default);
        await agents.AppendMessageAsync(root.Run.RunId, "agent_one", "message_one", "Follow up", default);
        var next = fixture.NewRun(3, 2, 1, idempotencyHashCharacter: 'c', requestHashCharacter: 'd');
        await agents.CreateContinuationAsync(root.Run.RunId, "agent_one", run.RunId, next, default);
        await fixture.PayloadStore.CaptureBaseContextAsync(next.Run.RunId, [], HarnessStoreTestFixture.Timestamp, default);
        var source = new SqliteModelContextSource(fixture.Options, fixture.PayloadStore);
        if (!complete)
        {
            var error = await Assert.ThrowsAsync<HarnessStoreException>(() => source.LoadAsync(next.Run.RunId, default));
            Assert.Equal("subagent_continuation_history_incomplete", error.Code);
            return;
        }
        var context = await source.LoadAsync(next.Run.RunId, default);
        Assert.Equal(3, context.SessionBase.Count);
        Assert.DoesNotContain(context.SessionBase, entry => entry.Content?.Contains("Parent private transcript") == true);
        Assert.Contains(context.SessionBase, entry => entry.Content == "Child task 🐈");
        Assert.Equal("provider_read", Assert.Single(context.SessionBase, entry => entry.Kind == ModelContextEntryKind.AssistantStep)
            .AssistantStep!.ToolCalls.Single().ProviderCallId);
        var receipt = Assert.Single(context.SessionBase, entry => entry.Kind == ModelContextEntryKind.ToolResult);
        Assert.Equal("provider_read", receipt.ToolReceipt!.ProviderCallId);
        Assert.Equal("Nội dung tệp", receipt.Content);
        Assert.Empty(context.RunLocal);
        Assert.Equal(Path.Combine("src", ".ru-instruction-scope"), Assert.Single(context.ObservedFilePaths));
        await events.CommitAsync(SubagentAdmission.CreateTerminalCommit(next.Run, new("evt_" + new string('e', 32)),
            false, "fixture_failure", HarnessStoreTestFixture.Timestamp), default);
        await agents.AppendMessageAsync(root.Run.RunId, "agent_one", "message_two", "Another follow up", default);
        var third = fixture.NewRun(4, 2, 1, idempotencyHashCharacter: 'e', requestHashCharacter: 'f');
        await agents.CreateContinuationAsync(root.Run.RunId, "agent_one", next.Run.RunId, third, default);
        await fixture.PayloadStore.CaptureBaseContextAsync(third.Run.RunId, [], HarnessStoreTestFixture.Timestamp, default);
        var thirdContext = await source.LoadAsync(third.Run.RunId, default);
        Assert.Equal(3, thirdContext.SessionBase.Count);
        Assert.Equal(3, thirdContext.SessionBase.Select(entry => entry.SourceId).Distinct().Count());
        Assert.Equal(context.ObservedFilePaths, thirdContext.ObservedFilePaths);
        await events.CommitAsync(SubagentAdmission.CreateTerminalCommit(third.Run, new("evt_" + new string('d', 32)),
            false, "fixture_failure", HarnessStoreTestFixture.Timestamp), default);
        await agents.AppendMessageAsync(root.Run.RunId, "agent_one", "message_three", "One too many", default);
        var fourth = fixture.NewRun(5, 2, 1, idempotencyHashCharacter: '1', requestHashCharacter: '2');
        var limit = await Assert.ThrowsAsync<HarnessStoreException>(() => agents.CreateContinuationAsync(
            root.Run.RunId, "agent_one", third.Run.RunId, fourth, default));
        Assert.Equal("subagent_attempt_limit", limit.Code);
    }
}
