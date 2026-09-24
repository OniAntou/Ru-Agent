using System.Text.Json;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class AgentAssistantDeltaModelTests
{
    private const string StepId = "step_0123456789abcdef0123456789abcdef";
    private const string ExecutionId = "exec_abcdef0123456789abcdef0123456789";

    [Fact]
    public void TryParse_accepts_bounded_utf8_delta_with_optional_execution_id()
    {
        var frame = Frame(
            7,
            new
            {
                text = "Xin chào anh",
                model_step_id = StepId,
                execution_id = ExecutionId,
            });

        Assert.True(AgentAssistantDeltaModel.TryParse(frame, out var delta));
        Assert.NotNull(delta);
        Assert.Equal("Xin chào anh", delta.Text);
        Assert.Equal(StepId, delta.ModelStepId);
        Assert.Equal(ExecutionId, delta.ExecutionId);
        Assert.Equal(7, delta.TransientSequence);
    }

    [Fact]
    public void TryParse_accepts_model_delta_without_execution_id()
    {
        var frame = Frame(1, new { text = "hello", model_step_id = StepId, execution_id = (string?)null });

        Assert.True(AgentAssistantDeltaModel.TryParse(frame, out var delta));
        Assert.Null(delta!.ExecutionId);
    }

    [Theory]
    [InlineData(false, "assistant.delta", 1, "{\"text\":\"hello\",\"model_step_id\":\"step_0123456789abcdef0123456789abcdef\"}")]
    [InlineData(true, "tool.output.delta", 1, "{\"text\":\"hello\",\"model_step_id\":\"step_0123456789abcdef0123456789abcdef\"}")]
    [InlineData(true, "assistant.delta", 0, "{\"text\":\"hello\",\"model_step_id\":\"step_0123456789abcdef0123456789abcdef\"}")]
    [InlineData(true, "assistant.delta", 1, "{\"model_step_id\":\"step_0123456789abcdef0123456789abcdef\"}")]
    [InlineData(true, "assistant.delta", 1, "{\"text\":\"\",\"model_step_id\":\"step_0123456789abcdef0123456789abcdef\"}")]
    [InlineData(true, "assistant.delta", 1, "{\"text\":\"hello\",\"model_step_id\":\"step_bad\"}")]
    [InlineData(true, "assistant.delta", 1, "{\"text\":\"hello\",\"model_step_id\":\"step_0123456789abcdef0123456789abcdef\",\"extra\":true}")]
    public void TryParse_rejects_invalid_or_untrusted_frames(
        bool isTransient,
        string eventType,
        long sequence,
        string inner)
    {
        var frame = new SseFrame(
            null,
            eventType,
            JsonSerializer.Serialize(new { kind = "assistant.delta", data = inner }),
            isTransient,
            sequence);

        Assert.False(AgentAssistantDeltaModel.TryParse(frame, out var delta));
        Assert.Null(delta);
    }

    [Fact]
    public void TryParse_rejects_over_limit_text_and_missing_transient_sequence()
    {
        var overLimit = Frame(
            2,
            new
            {
                text = new string('x', AgentAssistantDeltaModel.MaximumTextCharacters + 1),
                model_step_id = StepId,
            });
        var missingSequence = new SseFrame(
            null,
            "assistant.delta",
            overLimit.Data,
            true,
            null);

        Assert.False(AgentAssistantDeltaModel.TryParse(overLimit, out _));
        Assert.False(AgentAssistantDeltaModel.TryParse(missingSequence, out _));
    }

    [Fact]
    public void TryParse_rejects_invalid_execution_id_and_duplicate_fields()
    {
        var invalidExecution = Frame(
            3,
            new
            {
                text = "hello",
                model_step_id = StepId,
                execution_id = "exec_bad",
            });
        var duplicate = new SseFrame(
            null,
            "assistant.delta",
            "{\"kind\":\"assistant.delta\",\"data\":\"{\\\"text\\\":\\\"hello\\\",\\\"text\\\":\\\"again\\\",\\\"model_step_id\\\":\\\"" + StepId + "\\\"}\"}",
            true,
            4);

        Assert.False(AgentAssistantDeltaModel.TryParse(invalidExecution, out _));
        Assert.False(AgentAssistantDeltaModel.TryParse(duplicate, out _));
    }

    [Fact]
    public void TryParse_accepts_a_reset_frame_and_keeps_ordinary_frames_reset_free()
    {
        // The host emits a reset frame before a retried model step so the client
        // drops the superseded attempt's partial text. Ordinary frames must stay
        // exactly as they were: the parser rejects unknown fields.
        var reset = Frame(5, new { text = string.Empty, model_step_id = StepId, reset = true });
        var ordinary = Frame(6, new { text = "hello", model_step_id = StepId });

        Assert.True(AgentAssistantDeltaModel.TryParse(reset, out var parsedReset));
        Assert.True(parsedReset!.IsReset);
        Assert.Equal(string.Empty, parsedReset.Text);

        Assert.True(AgentAssistantDeltaModel.TryParse(ordinary, out var parsedOrdinary));
        Assert.False(parsedOrdinary!.IsReset);
        Assert.Equal("hello", parsedOrdinary.Text);
    }

    [Fact]
    public void TryParse_accepts_an_explicit_null_reset_as_an_ordinary_frame()
    {
        // The worker serializes unset optionals explicitly: a null reset is an
        // ordinary delta, and dropping it silences the whole turn.
        var frame = Frame(8, new { text = "Hi", model_step_id = StepId, execution_id = (string?)null, reset = (bool?)null });

        Assert.True(AgentAssistantDeltaModel.TryParse(frame, out var delta));
        Assert.NotNull(delta);
        Assert.False(delta.IsReset);
        Assert.Equal("Hi", delta.Text);
    }

    [Fact]
    public void TryParse_rejects_a_reset_frame_that_carries_text()
    {
        var contradictory = Frame(7, new { text = "leaked", model_step_id = StepId, reset = true });

        Assert.False(AgentAssistantDeltaModel.TryParse(contradictory, out _));
    }

    private static SseFrame Frame(long sequence, object inner) =>
        new(
            null,
            "assistant.delta",
            JsonSerializer.Serialize(new
            {
                kind = "assistant.delta",
                data = JsonSerializer.Serialize(inner),
            }),
            true,
            sequence);
}
