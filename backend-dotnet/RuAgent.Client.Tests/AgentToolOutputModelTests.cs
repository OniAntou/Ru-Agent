using System.Text.Json;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class AgentToolOutputModelTests
{
    private const string StepId = "step_0123456789abcdef0123456789abcdef";
    private const string ExecutionId = "exec_abcdef0123456789abcdef0123456789";

    [Fact]
    public void TryParse_accepts_bounded_output_with_execution_id()
    {
        var frame = Frame(
            9,
            new
            {
                text = "total 3\n-rw-r--r-- 1 user file.txt",
                model_step_id = StepId,
                execution_id = ExecutionId,
            });

        Assert.True(AgentToolOutputModel.TryParse(frame, out var output));
        Assert.NotNull(output);
        Assert.StartsWith("total 3", output.Text, StringComparison.Ordinal);
        Assert.Equal(ExecutionId, output.ExecutionId);
        Assert.Equal(StepId, output.ModelStepId);
        Assert.Equal(9, output.TransientSequence);
    }

    [Fact]
    public void TryParse_accepts_output_without_model_step_id()
    {
        var frame = Frame(
            2,
            new
            {
                text = "ok",
                model_step_id = (string?)null,
                execution_id = ExecutionId,
            });

        Assert.True(AgentToolOutputModel.TryParse(frame, out var output));
        Assert.Null(output!.ModelStepId);
    }

    [Fact]
    public void TryParse_accepts_the_host_shape_with_explicit_null_reset()
    {
        // The host's live publisher always writes the reset field, as null for
        // an ordinary frame; the parser must accept the shape it actually emits.
        var frame = Frame(
            3,
            new
            {
                text = "ready",
                model_step_id = (string?)null,
                execution_id = ExecutionId,
                reset = (bool?)null,
            });

        Assert.True(AgentToolOutputModel.TryParse(frame, out var output));
        Assert.False(output!.IsReset);
        Assert.Equal("ready", output.Text);
    }

    [Fact]
    public void TryParse_accepts_a_reset_frame_with_no_text()
    {
        var frame = Frame(
            4,
            new
            {
                text = "",
                model_step_id = (string?)null,
                execution_id = ExecutionId,
                reset = true,
            });

        Assert.True(AgentToolOutputModel.TryParse(frame, out var output));
        Assert.True(output!.IsReset);
        Assert.Equal(string.Empty, output.Text);
    }

    [Fact]
    public void TryParse_rejects_a_reset_frame_that_carries_text()
    {
        var frame = Frame(
            5,
            new
            {
                text = "not empty",
                model_step_id = (string?)null,
                execution_id = ExecutionId,
                reset = true,
            });

        Assert.False(AgentToolOutputModel.TryParse(frame, out _));
    }

    [Theory]
    [InlineData(true, "assistant.delta", 1, "{\"text\":\"hello\",\"execution_id\":\"exec_abcdef0123456789abcdef0123456789\"}")]
    [InlineData(true, "tool.output.delta", 0, "{\"text\":\"hello\",\"execution_id\":\"exec_abcdef0123456789abcdef0123456789\"}")]
    [InlineData(true, "tool.output.delta", 1, "{\"text\":\"hello\"}")]
    [InlineData(true, "tool.output.delta", 1, "{\"text\":\"\",\"execution_id\":\"exec_abcdef0123456789abcdef0123456789\"}")]
    [InlineData(true, "tool.output.delta", 1, "{\"text\":\"hello\",\"execution_id\":\"exec_bad\"}")]
    [InlineData(true, "tool.output.delta", 1, "{\"text\":\"hello\",\"execution_id\":\"exec_abcdef0123456789abcdef0123456789\",\"extra\":true}")]
    [InlineData(false, "tool.output.delta", 1, "{\"text\":\"hello\",\"execution_id\":\"exec_abcdef0123456789abcdef0123456789\"}")]
    public void TryParse_rejects_invalid_or_untrusted_frames(
        bool isTransient,
        string eventType,
        long sequence,
        string inner)
    {
        var frame = new SseFrame(
            null,
            eventType,
            JsonSerializer.Serialize(new { kind = "tool.output.delta", data = inner }),
            isTransient,
            sequence);

        Assert.False(AgentToolOutputModel.TryParse(frame, out _));
    }

    private static SseFrame Frame(long sequence, object inner) =>
        new(
            null,
            "tool.output.delta",
            JsonSerializer.Serialize(new
            {
                kind = "tool.output.delta",
                data = JsonSerializer.Serialize(inner),
            }),
            true,
            sequence);
}
