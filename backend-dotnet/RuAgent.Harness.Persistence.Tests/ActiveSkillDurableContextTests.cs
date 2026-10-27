using System.Security.Cryptography;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Context;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed partial class ModelContextSourceTests
{
    [Fact]
    public async Task Successful_native_skill_receipt_projects_from_durable_call_result_pair()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await store.TryCreateRunAsync(request, default);
        await fixture.PayloadStore.CaptureBaseContextAsync(request.Run.RunId, [], HarnessStoreTestFixture.Timestamp, default);

        using var arguments = System.Text.Json.JsonDocument.Parse("{\"name\":\"native-demo\"}");
        var step = new AssistantStep(null, [new ModelToolCall("provider_skill", "skill", "1.0.0", arguments.RootElement)],
            AssistantFinishReason.ToolCalls, 0);
        var assistantPayload = new HarnessPayloadIntent(HarnessStoreTestFixture.Payload(52), HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext, HarnessPayloadEncoding.Json,
            4096, AssistantStepJsonCodec.Serialize(step));
        const string body = "Native skill workflow body.";
        var outputText = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = "native-demo", name = "Native Demo", description = "Description", body,
            body_sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant(),
            source_path = "C:\\workspace\\.agents\\skills\\native-demo\\SKILL.md",
            resource_base = "C:\\workspace\\.agents\\skills\\native-demo", revision = "revision-1"
        });
        var outputBytes = Encoding.UTF8.GetBytes(outputText);
        var outputPayload = new HarnessPayloadIntent(HarnessStoreTestFixture.Payload(53), HarnessPayloadKind.ToolOutput,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json, outputBytes.Length, outputBytes);
        var stepId = new ModelStepId("step_55555555555555555555555555555555");
        var outcome = await store.CommitAsync(new HarnessCommit(request.Run.RunId, 0,
            NextRun(request.Run, 1, 3),
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(52), HarnessEventKind.ModelStepCompleted,
                 modelStepId: stepId, payloadId: assistantPayload.PayloadId),
             new HarnessEventIntent(HarnessStoreTestFixture.Event(53), HarnessEventKind.ToolExecutionCompleted,
                 modelStepId: stepId, toolCallId: new ToolCallId("call_55555555555555555555555555555555"),
                 providerCallId: "provider_skill", executionId: new ExecutionId("exec_55555555555555555555555555555555"),
                 payloadId: outputPayload.PayloadId, safeCode: "ok", toolOutcome: ToolExecutionOutcome.Succeeded,
                 sideEffectsPossible: false)], [assistantPayload, outputPayload], []), default);
        Assert.Equal(HarnessCommitOutcome.Committed, outcome.Outcome);

        var cachedAssembler = new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore));
        var warm = await cachedAssembler.AssembleAsync(request.Run.RunId, default);
        Assert.DoesNotContain(warm.Entries, entry => entry.SourceId == "base:active-skills");
        Assert.Contains("Native skill workflow body.", Encoding.UTF8.GetString(warm.CanonicalUtf8.Span), StringComparison.Ordinal);
        var cold = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.Run.RunId, default);
        Assert.Equal(warm.CanonicalUtf8.ToArray(), cold.CanonicalUtf8.ToArray());
    }

    [Fact]
    public async Task Explicit_skill_snapshot_survives_compaction_warm_cache_and_cold_restart()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await store.TryCreateRunAsync(request, default);
        await fixture.PayloadStore.CaptureBaseContextAsync(request.Run.RunId, [],
            HarnessStoreTestFixture.Timestamp, default);

        const string body = "Use the provided $ARGUMENTS while editing the feature.";
        var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var encoded = ActiveSkillSnapshots.EncodeExplicit("feature-review", "Feature review", "Review workflow",
            body, bodyHash, "C:\\workspace\\.agents\\skills\\feature-review\\SKILL.md",
            "C:\\workspace\\.agents\\skills\\feature-review", "revision-1", "src/app.cs");
        var payload = new HarnessPayloadIntent(HarnessStoreTestFixture.Payload(50), HarnessPayloadKind.SkillInvocation,
            HarnessPayloadAudience.ModelContext, HarnessPayloadEncoding.Utf8Text, 512 * 1024,
            Encoding.UTF8.GetBytes(encoded));
        var eventId = HarnessStoreTestFixture.Event(50);
        var committed = await store.CommitAsync(new HarnessCommit(request.Run.RunId, 0,
            NextRun(request.Run, version: 1, lastSequence: 2),
            [new HarnessEventIntent(eventId, HarnessEventKind.RunNoteAdded, payloadId: payload.PayloadId,
                safeCode: "skill_invoked")], [payload], []), default);
        Assert.Equal(HarnessCommitOutcome.Committed, committed.Outcome);

        var warmAssembler = new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore));
        var beforeCompaction = await warmAssembler.AssembleAsync(request.Run.RunId, default);
        Assert.Contains("src/app.cs", Assert.Single(beforeCompaction.Entries).Content, StringComparison.Ordinal);
        Assert.DoesNotContain(beforeCompaction.Entries, entry => entry.SourceId == $"run:invoked-skills:{eventId.Value}");

        var compactedRun = RunAt(request.Run, version: 2, lastSequence: 3,
            updatedAt: HarnessStoreTestFixture.Timestamp.AddSeconds(2));
        await CommitCompactionAsync(store, request.Run.RunId, compactedRun, expectedVersion: 1, eventNumber: 51,
            "skill-checkpoint", "A checkpoint retained the current task.", $"run:invoked-skills:{eventId.Value}",
            HarnessStoreTestFixture.Timestamp.AddSeconds(2), coveredSequence: 2);

        var warm = await warmAssembler.AssembleAsync(request.Run.RunId, default);
        var cold = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.Run.RunId, default);
        Assert.Equal(cold.CanonicalUtf8.ToArray(), warm.CanonicalUtf8.ToArray());
        var workflow = Assert.Single(cold.Entries, entry => entry.SourceId == "base:active-skills");
        Assert.Contains("src/app.cs", workflow.Content, StringComparison.Ordinal);
        Assert.Contains("src/app.cs while editing", workflow.Content, StringComparison.Ordinal);
        Assert.Contains("workflow", workflow.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(cold.Entries, entry => ActiveSkillSnapshots.IsReservedSourceId(entry.SourceId));
    }
}
