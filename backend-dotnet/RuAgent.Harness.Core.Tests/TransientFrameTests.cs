using System;
using RuAgent.Harness.Core;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

/// <summary>
/// The transient frames' newer identities: a reasoning delta carries its own kind, and a
/// reasoning preview frame names the activity row it belongs to.
/// </summary>
public sealed class TransientFrameTests
{
    private static readonly RunId Run = new("run_" + new string('b', 32));
    private static readonly ModelStepId Step = new("step_" + new string('a', 32));
    private static readonly string ActivityId = "act_" + new string('a', 32);

    [Fact]
    public void An_assistant_delta_defaults_to_text_and_reasoning_keeps_its_kind()
    {
        var text = new AssistantDelta(Step, 1, "answer");
        var reasoning = new AssistantDelta(Step, 2, "thinking", kind: AssistantDeltaKind.Reasoning);

        Assert.Equal(AssistantDeltaKind.Text, text.Kind);
        Assert.Equal(AssistantDeltaKind.Reasoning, reasoning.Kind);
    }

    [Fact]
    public void A_reasoning_delta_is_bounded_like_an_assistant_delta()
    {
        Assert.Throws<ArgumentException>(() =>
            new AssistantDelta(Step, 1, string.Empty, kind: AssistantDeltaKind.Reasoning));
        Assert.Throws<ArgumentException>(() =>
            new AssistantDelta(Step, 1, new string('x', 64 * 1024 + 1), kind: AssistantDeltaKind.Reasoning));
    }

    [Fact]
    public void A_reasoning_preview_frame_carries_its_activity_id()
    {
        var frame = new HarnessTransientFrame(
            Run, TransientFrameKind.ReasoningPreview, 1, "Investigating", modelStepId: Step, activityId: ActivityId);

        Assert.Equal(TransientFrameKind.ReasoningPreview, frame.Kind);
        Assert.Equal(ActivityId, frame.ActivityId);
        Assert.False(frame.IsReset);
    }

    [Fact]
    public void A_reasoning_preview_frame_rejects_a_row_id_that_is_not_an_activity_id()
    {
        Assert.Throws<ArgumentException>(() => new HarnessTransientFrame(
            Run, TransientFrameKind.ReasoningPreview, 1, "Investigating",
            modelStepId: Step, activityId: "step_" + new string('a', 32)));
    }

    [Fact]
    public void A_reasoning_preview_reset_carries_no_text_but_keeps_its_row_id()
    {
        var frame = new HarnessTransientFrame(
            Run, TransientFrameKind.ReasoningPreview, 2, string.Empty,
            modelStepId: Step, isReset: true, activityId: ActivityId);

        Assert.True(frame.IsReset);
        Assert.Equal(ActivityId, frame.ActivityId);
    }
}
