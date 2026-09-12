using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class AgentActivityModelTests
{
    [Fact]
    public void TryParse_reads_the_existing_transient_wrapper_and_keeps_cursor_separate()
    {
        var frame = Frame(
            "agent.activity",
            new
            {
                activity_id = "act_11111111111111111111111111111111",
                kind = "edit",
                state = "completed",
                file_name = "ChatView.xaml",
                line_start = 10,
                line_end = 12,
                added = 2,
                removed = 1,
                safe_code = "succeeded",
            },
            transientSequence: 7);

        var parsed = AgentActivityModel.TryParse(frame, out var activity);

        Assert.True(parsed);
        Assert.NotNull(activity);
        Assert.Equal("act_11111111111111111111111111111111", activity.ActivityId);
        Assert.Equal(AgentActivityKind.Edit, activity.Kind);
        Assert.Equal(AgentActivityState.Completed, activity.State);
        Assert.Equal("ChatView.xaml", activity.FileName);
        Assert.Equal(10, activity.LineStart);
        Assert.Equal(12, activity.LineEnd);
        Assert.Equal(2, activity.Added);
        Assert.Equal(1, activity.Removed);
        Assert.Equal(7, activity.TransientSequence);
        Assert.Null(activity.DurableSequence);
    }

    [Fact]
    public void TryParse_rejects_wrong_inner_kind_or_data_shape()
    {
        var wrongKind = Frame("assistant.delta", new { text = "private" }, 1);
        var wrongShape = new SseFrame(
            null,
            "transient",
            JsonSerializer.Serialize(new
            {
                kind = "agent.activity",
                data = new { activity_id = "act_1" },
            }),
            true,
            2);

        Assert.False(AgentActivityModel.TryParse(wrongKind, out _));
        Assert.False(AgentActivityModel.TryParse(wrongShape, out _));
    }

    [Fact]
    public void TryParse_ignores_non_transient_frames_and_other_transient_output()
    {
        var durable = new SseFrame(12, "message", "{}", false, null);
        var output = Frame("tool.output.delta", new { text = "private" }, 3);

        Assert.False(AgentActivityModel.TryParse(durable, out _));
        Assert.False(AgentActivityModel.TryParse(output, out _));
    }

    [Theory]
    [InlineData("{\"activity_id\":\"act_1\",\"kind\":\"agent.activity\",\"state\":\"completed\",\"safe_code\":\"succeeded\",\"command\":\"secret\"}")]
    [InlineData("{\"activity_id\":\"act_1\",\"kind\":\"agent.activity\",\"state\":\"completed\",\"safe_code\":\"succeeded\",\"unknown\":true}")]
    [InlineData("not-json")]
    public void TryParse_ignores_malformed_or_private_activity_payloads(string innerJson)
    {
        var outer = JsonSerializer.Serialize(new { kind = "agent.activity", data = innerJson });
        var frame = new SseFrame(null, "transient", outer, true, 4);

        Assert.False(AgentActivityModel.TryParse(frame, out var activity));
        Assert.Null(activity);
    }

    [Fact]
    public void TryParse_Preserves_ToolDisplay()
    {
        var frame = Frame("agent.activity", new { activity_id = "act_11111111111111111111111111111111", kind = "analyze", state = "completed", tool_display = "View", safe_code = "succeeded" }, transientSequence: 7);
        Assert.True(AgentActivityModel.TryParse(frame, out var model));
        Assert.Equal("View", model!.ToolDisplay);
    }

    [Fact]
    public void TryParse_Preserves_Detail_and_defaults_it_to_null()
    {
        var withDetail = Frame("agent.activity", new { activity_id = "act_11111111111111111111111111111111", kind = "thinking", state = "completed", safe_code = "succeeded", detail = "Considering folder scope." }, transientSequence: 7);
        Assert.True(AgentActivityModel.TryParse(withDetail, out var detailed));
        Assert.Equal("Considering folder scope.", detailed!.Detail);

        var withoutDetail = Frame("agent.activity", new { activity_id = "act_11111111111111111111111111111111", kind = "thinking", state = "completed", safe_code = "succeeded" }, transientSequence: 8);
        Assert.True(AgentActivityModel.TryParse(withoutDetail, out var plain));
        Assert.Null(plain!.Detail);
    }

    private static SseFrame Frame(string kind, object data, long transientSequence)
    {
        var innerJson = JsonSerializer.Serialize(data);
        var outerJson = JsonSerializer.Serialize(new { kind, data = innerJson });
        return new SseFrame(null, "transient", outerJson, true, transientSequence);
    }
}
