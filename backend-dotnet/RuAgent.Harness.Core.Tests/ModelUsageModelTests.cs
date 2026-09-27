using System.Text.Json;
using RuAgent.Harness.Core;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

/// <summary>
/// The model id that travels with a usage report, and the older payloads that do not
/// carry one.
/// </summary>
/// <remarks>
/// A token count is only worth anything next to what that model charges, so the model
/// has to survive the round trip into the durable payload. The field is additive and
/// optional, which means every report written before it existed still has to read back
/// — as a run whose price nobody can name, never as a failure.
/// </remarks>
public sealed class ModelUsageModelTests
{
    [Fact]
    public void A_model_survives_the_round_trip()
    {
        var step = new AssistantStep(
            "done",
            [],
            AssistantFinishReason.Stop,
            estimatedInputTokens: 17,
            usage: new ModelUsage(100, 20, 120, 30, 4, "vendor/paid"));

        var restored = AssistantStepJsonCodec.Deserialize(AssistantStepJsonCodec.Serialize(step));

        Assert.NotNull(restored.Usage);
        Assert.Equal("vendor/paid", restored.Usage!.Model);
        Assert.Equal(100, restored.Usage.InputTokens);
    }

    [Fact]
    public void An_absent_model_stays_absent_rather_than_becoming_an_empty_string()
    {
        var step = new AssistantStep(
            "done",
            [],
            AssistantFinishReason.Stop,
            estimatedInputTokens: 17,
            usage: new ModelUsage(100, 20, 120, null, null));

        using var document = JsonDocument.Parse(AssistantStepJsonCodec.Serialize(step));
        Assert.False(document.RootElement.GetProperty("usage").TryGetProperty("model", out _));
        Assert.Null(AssistantStepJsonCodec.Deserialize(AssistantStepJsonCodec.Serialize(step)).Usage!.Model);
    }

    [Fact]
    public void A_payload_written_before_the_field_existed_still_reads()
    {
        // The stored shape before this field existed. It must keep working, and it must
        // read as a run nobody can price rather than as a corrupt one.
        const string json = """
            {"text":"done","tool_calls":[],"finish_reason":"stop","estimated_input_tokens":17,
             "usage":{"input_tokens":100,"output_tokens":20,"total_tokens":120}}
            """;

        var restored = AssistantStepJsonCodec.Deserialize(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.NotNull(restored.Usage);
        Assert.Equal(100, restored.Usage!.InputTokens);
        Assert.Null(restored.Usage.Model);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nmodel")]
    public void An_unusable_model_name_is_treated_as_no_name(string model)
    {
        // A model name is never worth failing a usage payload over, and a name no price
        // table can match is the same as no name.
        var usage = new ModelUsage(1, 1, 2, null, null, model);

        Assert.Null(usage.Model);
        Assert.Equal(2, usage.TotalTokens);
    }

    [Fact]
    public void A_model_name_longer_than_a_bounded_identifier_is_dropped()
    {
        // The bound matches the catalog's own model-id limit, so a name that can be
        // published can also be stored and looked up.
        Assert.Null(new ModelUsage(1, 1, 2, null, null, new string('m', 257)).Model);
        Assert.Equal(new string('m', 256), new ModelUsage(1, 1, 2, null, null, new string('m', 256)).Model);
    }

    [Fact]
    public void Counts_are_unchanged_by_the_model_arriving_alongside_them()
    {
        // The whole point of the field is that a price can be attached to a count. A
        // count that moved because a name came with it would be worse than no price.
        var bare = new ModelUsage(700, 100, 800, 30, 4);
        var named = new ModelUsage(700, 100, 800, 30, 4, "vendor/paid");

        Assert.Equal(bare.InputTokens, named.InputTokens);
        Assert.Equal(bare.OutputTokens, named.OutputTokens);
        Assert.Equal(bare.TotalTokens, named.TotalTokens);
        Assert.Equal(bare.CacheReadInputTokens, named.CacheReadInputTokens);
        Assert.Equal(bare.CacheWriteInputTokens, named.CacheWriteInputTokens);
    }
}
