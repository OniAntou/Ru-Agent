using System.Text.Json;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class AgentReasoningPreviewModelTests
{
    private const string StepId = "step_0123456789abcdef0123456789abcdef";
    private const string ActivityId = "act_0123456789abcdef0123456789abcdef";

    [Fact]
    public void TryParse_accepts_a_bounded_line_for_an_activity_row()
    {
        var frame = Frame(7, new { activity_id = ActivityId, model_step_id = StepId, text = "Xin chào anh" });

        Assert.True(AgentReasoningPreviewModel.TryParse(frame, out var preview));
        Assert.NotNull(preview);
        Assert.Equal(ActivityId, preview.ActivityId);
        Assert.Equal(StepId, preview.ModelStepId);
        Assert.Equal("Xin chào anh", preview.Text);
        Assert.Equal(7, preview.TransientSequence);
        Assert.False(preview.IsReset);
    }

    [Fact]
    public void TryParse_accepts_a_reset_without_a_line()
    {
        var frame = Frame(8, new { activity_id = ActivityId, text = (string?)null, reset = true });

        Assert.True(AgentReasoningPreviewModel.TryParse(frame, out var preview));
        Assert.NotNull(preview);
        Assert.True(preview.IsReset);
        Assert.Equal(string.Empty, preview.Text);
        Assert.Null(preview.ModelStepId);
    }

    [Theory]
    [InlineData(false, "agent.reasoning_preview", 1, "{\"activity_id\":\"act_0123456789abcdef0123456789abcdef\",\"text\":\"hello\"}")]
    [InlineData(true, "agent.reasoning_preview", 0, "{\"activity_id\":\"act_0123456789abcdef0123456789abcdef\",\"text\":\"hello\"}")]
    [InlineData(true, "agent.reasoning_preview", 1, "{\"activity_id\":\"act_0123456789abcdef0123456789abcdef\"}")]
    [InlineData(true, "agent.reasoning_preview", 1, "{\"text\":\"hello\"}")]
    [InlineData(true, "agent.reasoning_preview", 1, "{\"activity_id\":\"step_0123456789abcdef0123456789abcdef\",\"text\":\"hello\"}")]
    [InlineData(true, "agent.reasoning_preview", 1, "{\"activity_id\":\"act_0123456789abcdef0123456789abcdef\",\"text\":\"hello\",\"extra\":true}")]
    [InlineData(true, "agent.reasoning_preview", 1, "{\"activity_id\":\"act_0123456789abcdef0123456789abcdef\",\"text\":\"hello\",\"reset\":true}")]
    [InlineData(true, "agent.reasoning_preview", 1, "{\"activity_id\":\"act_0123456789abcdef0123456789abcdef\",\"text\":\"a\\u0000b\"}")]
    public void TryParse_rejects_invalid_or_untrusted_frames(
        bool isTransient,
        string eventType,
        long sequence,
        string inner)
    {
        var frame = new SseFrame(
            null,
            eventType,
            JsonSerializer.Serialize(new { kind = "agent.reasoning_preview", data = inner }),
            isTransient,
            sequence);

        Assert.False(AgentReasoningPreviewModel.TryParse(frame, out var preview));
        Assert.Null(preview);
    }

    [Fact]
    public void TryParse_rejects_a_line_over_the_bounded_length()
    {
        var frame = Frame(1, new { activity_id = ActivityId, text = new string('x', AgentReasoningPreviewModel.MaximumTextCharacters + 1) });

        Assert.False(AgentReasoningPreviewModel.TryParse(frame, out _));
    }

    [Fact]
    public void TryParse_rejects_a_malformed_model_step_identity()
    {
        var frame = Frame(1, new { activity_id = ActivityId, model_step_id = "step_bad", text = "hello" });

        Assert.False(AgentReasoningPreviewModel.TryParse(frame, out _));
    }

    private static SseFrame Frame(long sequence, object inner) =>
        new(
            null,
            "agent.reasoning_preview",
            JsonSerializer.Serialize(new
            {
                kind = "agent.reasoning_preview",
                data = JsonSerializer.Serialize(inner),
            }),
            true,
            sequence);
}
