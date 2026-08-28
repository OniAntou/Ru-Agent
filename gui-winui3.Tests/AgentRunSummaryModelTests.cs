using System.Text.Json;
using RuAgent.Client;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentRunSummaryModelTests
{
    [Fact]
    public void Parses_typed_transient_summary_without_advancing_durable_cursor()
    {
        var inner = "{\"status\":\"succeeded\",\"verification_state\":\"verified\",\"workspace_label\":\"Test Agent\",\"files_scanned\":5,\"files_changed\":1}";
        var outer = JsonSerializer.Serialize(new { kind = "agent.summary", data = inner });
        var frame = new SseFrame(null, "transient", outer, true, 12);

        Assert.True(AgentRunSummaryModel.TryParse(frame, out var summary));
        Assert.NotNull(summary);
        Assert.Equal("succeeded", summary!.Status);
        Assert.Equal("verified", summary.VerificationState);
        Assert.Equal(5, summary.FilesScanned);
        Assert.Equal(1, summary.FilesChanged);
        Assert.Null(summary.DurableSequence);
        Assert.Equal(12, summary.TransientSequence);
    }

    [Theory]
    [InlineData("{\"kind\":\"agent.activity\",\"data\":\"{}\"}")]
    [InlineData("{\"kind\":\"agent.summary\",\"data\":{\"status\":\"failed\"}}")]
    [InlineData("{\"kind\":\"agent.summary\",\"data\":\"{\\\"status\\\":\\\"failed\\\",\\\"verification_state\\\":\\\"not_applicable\\\",\\\"output\\\":\\\"secret\\\"}\"}")]
    public void Rejects_wrong_shape_or_unsafe_summary(string outerJson)
    {
        var frame = new SseFrame(null, "transient", outerJson, true, 2);
        Assert.False(AgentRunSummaryModel.TryParse(frame, out var summary));
        Assert.Null(summary);
    }
}
