using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Protocol;
using RuAgent.Harness.Persistence.Context;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

/// <summary>
/// ADR 0026: a tool image is stored encrypted beside its tool result, model-only, and
/// comes back attached to that result; only the newest images are attached.
/// </summary>
public sealed class ToolImageContextTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public async Task Stored_tool_images_round_trip_and_only_the_newest_are_attached()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var created = fixture.NewRun(1, 1, 1);
        await store.TryCreateRunAsync(created, default);
        await fixture.PayloadStore.CaptureBaseContextAsync(created.Run.RunId, [], HarnessStoreTestFixture.Timestamp, default);
        var callCount = ToolOutputImage.MaximumInContext + 2;
        var calls = Enumerable.Range(1, callCount)
            .Select(index => new ModelToolCall(
                $"provider-call-{index}",
                "read",
                "1",
                System.Text.Json.JsonDocument.Parse($"{{\"path\":\"shot{index}.png\"}}").RootElement))
            .ToArray();
        var step = new AssistantStep("looking", calls, AssistantFinishReason.ToolCalls, estimatedInputTokens: 1);
        var stepPayload = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(20),
            HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json,
            4096,
            AssistantStepJsonCodec.Serialize(step));
        var stepRun = NextRun(created.Run, version: 1, lastSequence: 2);
        await store.CommitAsync(new HarnessCommit(created.Run.RunId, 0, stepRun,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(20), HarnessEventKind.ModelStepCompleted,
                modelStepId: new ModelStepId("step_11111111111111111111111111111111"), payloadId: stepPayload.PayloadId)],
            [stepPayload], []), default);

        var events = new List<HarnessEventIntent>();
        var payloads = new List<HarnessPayloadIntent>();
        var images = new List<byte[]>();
        for (var index = 1; index <= callCount; index++)
        {
            var output = new HarnessPayloadIntent(
                HarnessStoreTestFixture.Payload(100 + index),
                HarnessPayloadKind.ToolOutput,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Json,
                4096,
                Encoding.UTF8.GetBytes($"{{\"path\":\"shot{index}.png\",\"media_type\":\"image/png\"}}"));
            var image = Png((byte)index);
            images.Add(image);
            payloads.Add(output);
            payloads.Add(new HarnessPayloadIntent(
                ToolImagePayloadId.For(output.PayloadId),
                HarnessPayloadKind.ToolImage,
                HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Opaque,
                image.Length,
                image));
            events.Add(new HarnessEventIntent(
                HarnessStoreTestFixture.Event(100 + index),
                HarnessEventKind.ToolExecutionCompleted,
                toolCallId: new ToolCallId($"call_{index:D32}"),
                providerCallId: $"provider-call-{index}",
                executionId: new ExecutionId($"exec_{index:D32}"),
                payloadId: output.PayloadId,
                safeCode: "ok",
                toolOutcome: ToolExecutionOutcome.Succeeded,
                sideEffectsPossible: false));
        }

        await store.CommitAsync(new HarnessCommit(created.Run.RunId, 1,
            NextRun(stepRun, version: 2, lastSequence: 2 + callCount), events, payloads, []), default);

        var context = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(created.Run.RunId, default);

        var results = context.Entries.Where(entry => entry.Kind == ModelContextEntryKind.ToolResult).ToArray();
        Assert.Equal(callCount, results.Length);
        var omitted = callCount - ToolOutputImage.MaximumInContext;
        for (var index = 0; index < callCount; index++)
        {
            if (index < omitted)
            {
                Assert.True(results[index].ToolImageOmitted);
                Assert.Null(results[index].ToolImage);
            }
            else
            {
                Assert.False(results[index].ToolImageOmitted);
                Assert.Equal("image/png", results[index].ToolImage!.MediaType);
                Assert.Equal(images[index], results[index].ToolImage!.Bytes.ToArray());
            }
        }

        // Each attached image counts as one image allowance in the Core estimate.
        var textOnly = new ModelContextSnapshot(
            context.Entries.Select(entry => entry.Kind == ModelContextEntryKind.ToolResult
                ? ModelContextEntry.ToolResult(entry.SourceId, entry.Sequence, entry.ToolReceipt!, entry.Content!)
                : entry).ToArray(),
            context.CanonicalUtf8);
        Assert.Equal(
            ToolOutputImage.MaximumInContext * (long)RuAgent.Protocol.Harness.ContextTokenBreakdown.ImageTokenAllowance,
            ModelContextSizeEstimator.EstimateTokens(context) - ModelContextSizeEstimator.EstimateTokens(textOnly));

        // The image is model-only: a client read is refused.
        var imageId = ToolImagePayloadId.For(HarnessStoreTestFixture.Payload(100 + callCount));
        var forbidden = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            fixture.PayloadStore.ReadForClientAsync(created.Run.RunId, imageId, default));
        Assert.Equal("payload_client_forbidden", forbidden.Message);
        var model = await fixture.PayloadStore.ReadForModelAsync(created.Run.RunId, imageId, default);
        Assert.Equal(images[^1], model.Plaintext.ToArray());
    }

    [Fact]
    public void Tool_image_payloads_must_be_opaque_and_model_only()
    {
        var bytes = Png(1);
        Assert.Throws<ArgumentException>(() => new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(1), HarnessPayloadKind.ToolImage,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Opaque, bytes.Length, bytes));
        Assert.Throws<ArgumentException>(() => new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(1), HarnessPayloadKind.ToolImage,
            HarnessPayloadAudience.ModelContext, HarnessPayloadEncoding.Utf8Text, bytes.Length, bytes));
    }

    [Fact]
    public void Image_payload_id_is_derived_from_the_result_payload()
    {
        var first = ToolImagePayloadId.For(HarnessStoreTestFixture.Payload(1));

        Assert.Equal(first, ToolImagePayloadId.For(HarnessStoreTestFixture.Payload(1)));
        Assert.NotEqual(first, ToolImagePayloadId.For(HarnessStoreTestFixture.Payload(2)));
        Assert.NotEqual(HarnessStoreTestFixture.Payload(1), first);
    }

    private static byte[] Png(byte fill)
    {
        var bytes = new byte[256];
        Array.Fill(bytes, fill);
        PngSignature.CopyTo(bytes, 0);
        return bytes;
    }

    private static AgentRun NextRun(AgentRun current, long version, long lastSequence) =>
        new(
            current.RunId,
            current.SessionId,
            current.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.Running),
            version,
            lastSequence,
            current.BudgetWindow,
            current.ModelStepsInWindow,
            current.ToolCallsInWindow,
            current.Preset,
            current.PolicySnapshotVersion,
            current.PolicySnapshotSha256,
            current.CreatedAt,
            current.UpdatedAt.AddSeconds(version));
}
