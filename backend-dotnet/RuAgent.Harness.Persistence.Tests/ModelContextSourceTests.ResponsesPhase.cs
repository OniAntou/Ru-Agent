using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence.Context;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed partial class ModelContextSourceTests
{
    [Theory]
    [InlineData("commentary")]
    [InlineData("final_answer")]
    public async Task Fresh_sqlite_context_source_restores_phased_text_messages(string phase)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await store.TryCreateRunAsync(request, default);
        await fixture.PayloadStore.CaptureBaseContextAsync(request.Run.RunId, [], HarnessStoreTestFixture.Timestamp, default);
        var step = new AssistantStep("Checking.", [], AssistantFinishReason.Stop, 1,
            responsesMessages: new ResponsesAssistantMessages([new JsonObject
            {
                ["type"] = "message", ["id"] = "msg_1", ["role"] = "assistant", ["phase"] = phase,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "Checking." }),
            }]));
        var bytes = AssistantStepJsonCodec.Serialize(step);
        var payload = new HarnessPayloadIntent(HarnessStoreTestFixture.Payload(40), HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.ModelContext, HarnessPayloadEncoding.Json, bytes.Length, bytes);
        Assert.False(HarnessPayloadStore.CanReadForClient(payload));
        var commit = await store.CommitAsync(new HarnessCommit(request.Run.RunId, 0,
            NextRun(request.Run, version: 1, lastSequence: 2),
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(40), HarnessEventKind.ModelStepCompleted,
                modelStepId: new ModelStepId("step_" + new string('a', 32)), payloadId: payload.PayloadId)], [payload], []), default);
        Assert.Equal(HarnessCommitOutcome.Committed, commit.Outcome);
        var context = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.Run.RunId, default);
        var restored = Assert.Single(context.Entries).AssistantStep!;
        Assert.Equal(phase, restored.ResponsesMessages!.Messages[0]["phase"]!.GetValue<string>());
        Assert.Equal("Checking.", restored.ResponsesMessages.Text);
        Assert.Contains($"\"phase\":\"{phase}\"", System.Text.Encoding.UTF8.GetString(context.CanonicalUtf8.Span));
    }
}
