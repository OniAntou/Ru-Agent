using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Protocol.Harness;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// The host's side of the thinking preview: the transient frame maps to the
/// `agent.reasoning_preview` SSE kind, and a scripted model can stream reasoning for manual
/// and end-to-end checks.
/// </summary>
public sealed class HarnessReasoningPreviewTests
{
    private static readonly RunId Run = new("run_" + new string('b', 32));
    private static readonly ModelStepId Step = new("step_" + new string('a', 32));
    private static readonly string ActivityId = "act_" + new string('a', 32);

    [Fact]
    public async Task Live_frame_publishes_the_reasoning_preview_kind_and_payload()
    {
        var hub = new HarnessLiveEventHub();
        await using var lease = hub.Subscribe(Run);
        var frame = new HarnessTransientFrame(
            Run, TransientFrameKind.ReasoningPreview, 1, "Investigating the store",
            modelStepId: Step, activityId: ActivityId);

        AgentRuntimeCoordinatorFactory.PublishLiveFrame(hub, frame);

        Assert.True(lease.TryReadNext(out var published));
        Assert.Equal("agent.reasoning_preview", published.Kind);
        using var data = JsonDocument.Parse(published.Data);
        Assert.Equal(ActivityId, data.RootElement.GetProperty("activity_id").GetString());
        Assert.Equal(Step.Value, data.RootElement.GetProperty("model_step_id").GetString());
        Assert.Equal("Investigating the store", data.RootElement.GetProperty("text").GetString());
        Assert.False(data.RootElement.TryGetProperty("reset", out var reset) && reset.ValueKind == JsonValueKind.True);
    }

    [Fact]
    public async Task Live_frame_publishes_a_reset_without_a_line()
    {
        var hub = new HarnessLiveEventHub();
        await using var lease = hub.Subscribe(Run);
        var frame = new HarnessTransientFrame(
            Run, TransientFrameKind.ReasoningPreview, 2, string.Empty,
            modelStepId: Step, isReset: true, activityId: ActivityId);

        AgentRuntimeCoordinatorFactory.PublishLiveFrame(hub, frame);

        Assert.True(lease.TryReadNext(out var published));
        using var data = JsonDocument.Parse(published.Data);
        Assert.Equal(ActivityId, data.RootElement.GetProperty("activity_id").GetString());
        Assert.True(data.RootElement.GetProperty("reset").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.RootElement.GetProperty("text").ValueKind);
    }

    [Fact]
    public async Task A_scripted_reasoning_delay_paces_the_lines_for_manual_checks()
    {
        var runner = ScriptedModelStepRunner.FromJsonForTests(
            """[{"text":"done","reasoning":"first\nsecond\n","reasoning_delay_ms":40}]""");
        var request = new ModelStepRequest(
            Run,
            Step,
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), ReadOnlyMemory<byte>.Empty),
            AgentPermissionPreset.Standard,
            policySnapshotVersion: 1,
            policySnapshotSha256: new string('a', 64));
        var deltas = new List<AssistantDelta>();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await runner.RunStepAsync(
            request,
            delta =>
            {
                deltas.Add(delta);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(watch.ElapsedMilliseconds >= 60, $"elapsed {watch.ElapsedMilliseconds}ms");
        Assert.Equal(3, deltas.Count(delta => delta.Kind == AssistantDeltaKind.Reasoning));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2_001)]
    public void A_scripted_reasoning_delay_outside_the_bound_is_rejected(int delay)
    {
        Assert.Throws<InvalidOperationException>(() => ScriptedModelStepRunner.FromJsonForTests(
            $$"""[{"text":"done","reasoning":"first\n","reasoning_delay_ms":{{delay}}}]"""));
    }

    [Fact]
    public void A_scripted_reasoning_delay_without_reasoning_is_rejected() =>
        Assert.Throws<InvalidOperationException>(() => ScriptedModelStepRunner.FromJsonForTests(
            """[{"text":"done","reasoning_delay_ms":40}]"""));

    [Fact]
    public async Task A_scripted_step_streams_its_reasoning_lines_before_the_answer()
    {
        var runner = ScriptedModelStepRunner.FromJsonForTests(
            """[{"text":"done","reasoning":"first line\nsecond line"}]""");
        var request = new ModelStepRequest(
            Run,
            Step,
            new ModelContextSnapshot(Array.Empty<ModelContextEntry>(), ReadOnlyMemory<byte>.Empty),
            AgentPermissionPreset.Standard,
            policySnapshotVersion: 1,
            policySnapshotSha256: new string('a', 64));
        var deltas = new List<AssistantDelta>();

        var result = await runner.RunStepAsync(
            request,
            delta =>
            {
                deltas.Add(delta);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal("done", result.Step.Text);
        var reasoning = deltas.Where(delta => delta.Kind == AssistantDeltaKind.Reasoning).ToArray();
        Assert.Equal(["first line\n", "second line\n"], reasoning.Select(delta => delta.Content));
        Assert.All(reasoning, delta => Assert.False(delta.IsReset));
        Assert.Equal([1L, 2L], reasoning.Select(delta => delta.Sequence));
    }
}
