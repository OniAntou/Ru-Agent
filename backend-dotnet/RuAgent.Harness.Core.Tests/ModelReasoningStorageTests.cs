using System.Text;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

/// <summary>
/// A step's reasoning is stored whole up to its bound and replayed as an excerpt that
/// names it, instead of the last 8,000 characters cut silently to the first 4,000.
/// </summary>
public sealed class ModelReasoningStorageTests
{
    private static readonly ModelStepId Step = new("step_22222222222222222222222222222222");

    [Fact]
    public void Long_reasoning_is_stored_whole_from_its_first_word()
    {
        var reasoning = "First, the plan. " + new string('x', 60_000) + " Last word.";

        var stored = AgentRunStepHandlers.ClipReasoning(reasoning);

        Assert.Equal(reasoning, stored);
    }

    [Fact]
    public void Reasoning_past_the_bound_keeps_its_head_and_says_it_was_cut()
    {
        var reasoning = "Start. " + new string('é', ModelReasoningPayloadId.MaximumBytes);

        var stored = AgentRunStepHandlers.ClipReasoning(reasoning)!;

        Assert.StartsWith("Start. ", stored, StringComparison.Ordinal);
        Assert.EndsWith(ModelReasoningPayloadId.TruncationNote, stored, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(stored) <= ModelReasoningPayloadId.MaximumBytes);
    }

    [Fact]
    public void The_payload_id_is_derived_from_the_step_alone()
    {
        var id = ModelReasoningPayloadId.For(Step);

        Assert.Equal(id, ModelReasoningPayloadId.For(Step));
        Assert.NotEqual(id, ModelReasoningPayloadId.For(new ModelStepId("step_33333333333333333333333333333333")));
        Assert.Matches("^payload_[0-9a-f]{32}$", id.Value);
    }

    [Fact]
    public void A_cut_excerpt_ends_with_an_ellipsis_and_names_the_stored_reasoning()
    {
        var payload = ModelReasoningPayloadId.For(Step);
        var activity = AgentActivityProjector.ProjectModelStep(Step, AgentActivityState.Completed,
            detail: new string('a', 9_000), reasoningPayloadId: payload);

        Assert.Equal(AgentActivityWireValidator.MaximumDetailLength, activity.Detail!.Length);
        Assert.EndsWith("…", activity.Detail, StringComparison.Ordinal);
        Assert.Equal(payload.Value, activity.ReasoningPayloadId);
    }

    [Fact]
    public void A_whole_excerpt_names_no_payload()
    {
        var activity = AgentActivityProjector.ProjectModelStep(Step, AgentActivityState.Completed,
            detail: "Short reasoning.", reasoningPayloadId: ModelReasoningPayloadId.For(Step));

        Assert.Equal("Short reasoning.", activity.Detail);
        Assert.Null(activity.ReasoningPayloadId);
    }
}
