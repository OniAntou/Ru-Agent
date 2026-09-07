using System.Text.Json;
using RuAgent.Harness.Core;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class AssistantStepJsonCodecTests
{
    [Fact]
    public void Round_trip_preserves_exact_zero_usage()
    {
        var step = new AssistantStep(
            "done",
            [],
            AssistantFinishReason.Stop,
            estimatedInputTokens: 17,
            usage: new ModelUsage(0, 0, 0, 0, 0));

        var restored = AssistantStepJsonCodec.Deserialize(AssistantStepJsonCodec.Serialize(step));

        Assert.NotNull(restored.Usage);
        Assert.Equal(0, restored.Usage!.InputTokens);
        Assert.Equal(0, restored.Usage.OutputTokens);
        Assert.Equal(0, restored.Usage.TotalTokens);
        Assert.Equal(0, restored.Usage.CacheReadInputTokens);
        Assert.Equal(0, restored.Usage.CacheWriteInputTokens);
    }

    [Fact]
    public void Unknown_usage_is_omitted_and_round_trips_as_null()
    {
        var step = new AssistantStep("done", [], AssistantFinishReason.Stop, estimatedInputTokens: 17);

        var json = AssistantStepJsonCodec.Serialize(step);
        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.TryGetProperty("usage", out _));
        Assert.Null(AssistantStepJsonCodec.Deserialize(json).Usage);
    }

    [Fact]
    public void Older_payload_without_usage_remains_readable()
    {
        const string json = "{\"text\":\"done\",\"tool_calls\":[],\"finish_reason\":\"stop\",\"estimated_input_tokens\":17}";

        var restored = AssistantStepJsonCodec.Deserialize(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Equal(17, restored.EstimatedInputTokens);
        Assert.Null(restored.Usage);
    }
}
