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
