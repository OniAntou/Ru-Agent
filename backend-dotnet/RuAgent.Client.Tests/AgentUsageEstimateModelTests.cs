using System.Text.Json;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class AgentUsageEstimateModelTests
{
    private const string StepId = "step_0123456789abcdef0123456789abcdef";

    [Fact]
    public void Optional_context_partition_is_validated_and_travels_with_its_step()
    {
        var frame = Frame(7, new { estimated_input_tokens = 42, max_prompt_tokens = 100, model_step_id = StepId,
            context_tokens = new { system_prompt = 10, tool_definitions = 12, messages = 20, estimator = "request-bytes-v1" } });
        Assert.True(AgentUsageEstimateModel.TryParse(frame, out var estimate));
        Assert.Equal(42, estimate!.ContextTokens!.Total);
        Assert.Equal(12, estimate.ContextTokens.ToolDefinitions);
        var bad = Frame(8, new { estimated_input_tokens = 42, max_prompt_tokens = 100, model_step_id = StepId,
            context_tokens = new { system_prompt = -1, tool_definitions = 12, messages = 20, estimator = "request-bytes-v1" } });
        Assert.False(AgentUsageEstimateModel.TryParse(bad, out _));
    }

    [Fact]
    public void TryParse_accepts_the_measured_request_size_for_a_step()
    {
        var frame = Frame(3, new { estimated_input_tokens = 4_000, max_prompt_tokens = 200_000, model_step_id = StepId });

        Assert.True(AgentUsageEstimateModel.TryParse(frame, out var estimate));
        Assert.NotNull(estimate);
        Assert.Equal(4_000, estimate.EstimatedInputTokens);
        Assert.Equal(200_000, estimate.MaxPromptTokens);
        Assert.Equal(StepId, estimate.ModelStepId);
        Assert.Equal(3, estimate.TransientSequence);
    }

    [Fact]
    public void TryParse_reads_the_published_compaction_trigger_and_rejects_a_malformed_one()
    {
        var frame = Frame(5, new { estimated_input_tokens = 4_000, max_prompt_tokens = 200_000, compaction_trigger_tokens = 159_000, model_step_id = StepId });
        Assert.True(AgentUsageEstimateModel.TryParse(frame, out var estimate));
        Assert.Equal(159_000, estimate!.CompactionTriggerTokens);

        var absent = Frame(6, new { estimated_input_tokens = 4_000, max_prompt_tokens = 200_000, compaction_trigger_tokens = (long?)null, model_step_id = StepId });
        Assert.True(AgentUsageEstimateModel.TryParse(absent, out var none));
        Assert.Null(none!.CompactionTriggerTokens);

        var malformed = Frame(7, new { estimated_input_tokens = 4_000, max_prompt_tokens = 200_000, compaction_trigger_tokens = "soon", model_step_id = StepId });
        Assert.False(AgentUsageEstimateModel.TryParse(malformed, out _));
    }

    [Fact]
    public void TryParse_accepts_an_unknown_prompt_budget_so_the_client_falls_back_to_its_catalog()
    {
        var frame = Frame(4, new { estimated_input_tokens = 900, max_prompt_tokens = 0, model_step_id = StepId });

        Assert.True(AgentUsageEstimateModel.TryParse(frame, out var estimate));
        Assert.Equal(0, estimate!.MaxPromptTokens);
    }

    [Theory]
    [InlineData(false, "agent.usage_estimate", 1, "{\"estimated_input_tokens\":10,\"max_prompt_tokens\":20,\"model_step_id\":\"step_0123456789abcdef0123456789abcdef\"}")]
    [InlineData(true, "assistant.delta", 1, "{\"estimated_input_tokens\":10,\"max_prompt_tokens\":20,\"model_step_id\":\"step_0123456789abcdef0123456789abcdef\"}")]
    [InlineData(true, "agent.usage_estimate", 0, "{\"estimated_input_tokens\":10,\"max_prompt_tokens\":20,\"model_step_id\":\"step_0123456789abcdef0123456789abcdef\"}")]
    [InlineData(true, "agent.usage_estimate", 1, "{\"max_prompt_tokens\":20,\"model_step_id\":\"step_0123456789abcdef0123456789abcdef\"}")]
    [InlineData(true, "agent.usage_estimate", 1, "{\"estimated_input_tokens\":0,\"max_prompt_tokens\":20,\"model_step_id\":\"step_0123456789abcdef0123456789abcdef\"}")]
    [InlineData(true, "agent.usage_estimate", 1, "{\"estimated_input_tokens\":-5,\"max_prompt_tokens\":20,\"model_step_id\":\"step_0123456789abcdef0123456789abcdef\"}")]
    [InlineData(true, "agent.usage_estimate", 1, "{\"estimated_input_tokens\":10,\"max_prompt_tokens\":-1,\"model_step_id\":\"step_0123456789abcdef0123456789abcdef\"}")]
    [InlineData(true, "agent.usage_estimate", 1, "{\"estimated_input_tokens\":10,\"max_prompt_tokens\":20,\"model_step_id\":\"step_bad\"}")]
    [InlineData(true, "agent.usage_estimate", 1, "{\"estimated_input_tokens\":10,\"max_prompt_tokens\":20}")]
    [InlineData(true, "agent.usage_estimate", 1, "{\"estimated_input_tokens\":10,\"max_prompt_tokens\":20,\"model_step_id\":\"step_0123456789abcdef0123456789abcdef\",\"extra\":true}")]
    public void TryParse_rejects_invalid_or_untrusted_frames(
        bool isTransient,
        string eventType,
        long sequence,
        string inner)
    {
        var frame = new SseFrame(
            null,
            eventType,
            JsonSerializer.Serialize(new { kind = "agent.usage_estimate", data = inner }),
            isTransient,
            sequence);

        Assert.False(AgentUsageEstimateModel.TryParse(frame, out var estimate));
        Assert.Null(estimate);
    }

    [Fact]
    public void TryParse_rejects_duplicate_fields_and_a_missing_transient_sequence()
    {
        var duplicate = new SseFrame(
            null,
            "agent.usage_estimate",
            "{\"kind\":\"agent.usage_estimate\",\"data\":\"{\\\"estimated_input_tokens\\\":10,\\\"estimated_input_tokens\\\":11,\\\"max_prompt_tokens\\\":20,\\\"model_step_id\\\":\\\"" + StepId + "\\\"}\"}",
            true,
            5);
        var missingSequence = new SseFrame(
            null,
            "agent.usage_estimate",
            JsonSerializer.Serialize(new
            {
                kind = "agent.usage_estimate",
                data = JsonSerializer.Serialize(new
                {
                    estimated_input_tokens = 10,
                    max_prompt_tokens = 20,
                    model_step_id = StepId,
                }),
            }),
            true,
            null);

        Assert.False(AgentUsageEstimateModel.TryParse(duplicate, out _));
        Assert.False(AgentUsageEstimateModel.TryParse(missingSequence, out _));
    }

    private static SseFrame Frame(long sequence, object inner) =>
        new(
            null,
            "agent.usage_estimate",
            JsonSerializer.Serialize(new
            {
                kind = "agent.usage_estimate",
                data = JsonSerializer.Serialize(inner),
            }),
            true,
            sequence);
}
