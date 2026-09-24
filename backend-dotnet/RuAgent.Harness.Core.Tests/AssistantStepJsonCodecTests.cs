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
    public void Model_tool_call_requires_a_non_null_provider_call_id()
    {
        var arguments = JsonDocument.Parse("{}").RootElement.Clone();

        var error = Assert.Throws<ArgumentException>(() => new ModelToolCall(
            null!,
            "read",
            "1.0.0",
            arguments));

        Assert.Equal("providerCallId", error.ParamName);
    }

    [Fact]
    public void Deserialize_rejects_null_or_missing_provider_call_id()
    {
        var nullId = "{\"text\":null,\"tool_calls\":[{\"provider_call_id\":null,\"tool_id\":\"read\",\"tool_version\":\"1.0.0\",\"arguments\":{}}],\"finish_reason\":\"tool_calls\",\"estimated_input_tokens\":1}";
        var missingId = "{\"text\":null,\"tool_calls\":[{\"tool_id\":\"read\",\"tool_version\":\"1.0.0\",\"arguments\":{}}],\"finish_reason\":\"tool_calls\",\"estimated_input_tokens\":1}";

        Assert.Throws<InvalidDataException>(() => AssistantStepJsonCodec.Deserialize(System.Text.Encoding.UTF8.GetBytes(nullId)));
        Assert.Throws<InvalidDataException>(() => AssistantStepJsonCodec.Deserialize(System.Text.Encoding.UTF8.GetBytes(missingId)));
    }

    [Fact]
    public void Deserialize_rejects_unsafe_provider_call_id()
    {
        const string json = "{\"text\":null,\"tool_calls\":[{\"provider_call_id\":\"call/unsafe\",\"tool_id\":\"read\",\"tool_version\":\"1.0.0\",\"arguments\":{}}],\"finish_reason\":\"tool_calls\",\"estimated_input_tokens\":1}";

        Assert.Throws<InvalidDataException>(() =>
            AssistantStepJsonCodec.Deserialize(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Deserialize_rejects_duplicate_provider_call_ids()
    {
        const string json = "{\"text\":null,\"tool_calls\":[{\"provider_call_id\":\"call_1\",\"tool_id\":\"read\",\"tool_version\":\"1.0.0\",\"arguments\":{}},{\"provider_call_id\":\"call_1\",\"tool_id\":\"read\",\"tool_version\":\"1.0.0\",\"arguments\":{}}],\"finish_reason\":\"tool_calls\",\"estimated_input_tokens\":1}";

        Assert.Throws<InvalidDataException>(() =>
            AssistantStepJsonCodec.Deserialize(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void AssistantStep_rejects_duplicate_provider_call_ids_before_persistence()
    {
        Assert.Throws<ArgumentException>(() => new AssistantStep(
            null,
            [
                new ModelToolCall("call_1", "read", "1.0.0", JsonDocument.Parse("{}").RootElement),
                new ModelToolCall("call_1", "read", "1.0.0", JsonDocument.Parse("{}").RootElement),
            ],
            AssistantFinishReason.ToolCalls,
            estimatedInputTokens: 1));
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
