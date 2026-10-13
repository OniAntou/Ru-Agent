using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentLiveSegmentTests
{
    [Fact]
    public void View_drains_earlier_activities_before_accepting_later_text_and_renders_before_freeze()
    {
        var source = GuiSource.ViewText("ChatView");
        var delta = source[source.IndexOf("private void OnAgentAssistantDeltaReceived", StringComparison.Ordinal)..];
        Assert.True(delta.IndexOf("FlushAgentActivities();", StringComparison.Ordinal)
            < delta.IndexOf("bool accepted = delta.IsReset", StringComparison.Ordinal));
        var freeze = source[source.IndexOf("private void FreezeTextSegmentForActivity", StringComparison.Ordinal)..];
        Assert.True(freeze.IndexOf("_replyRenderScheduler.ApplyPending();", StringComparison.Ordinal)
            < freeze.IndexOf("AgentLiveSegmentBuilder.Freeze", StringComparison.Ordinal));
        Assert.True(freeze.IndexOf("FlushPendingReplyText();", StringComparison.Ordinal) >= 0
            && freeze.IndexOf("FlushPendingReplyText();", StringComparison.Ordinal)
                < freeze.IndexOf("AgentLiveSegmentBuilder.Freeze", StringComparison.Ordinal));
    }

    private static AgentActivityItem Activity(string id) => new(new AgentActivityModel(
        ActivityId: id, Kind: AgentActivityKind.Thinking, State: AgentActivityState.Started,
        FileName: null, LineStart: null, LineEnd: null, Symbol: null, Count: null,
        Added: null, Removed: null, DurationMs: null, SafeCode: "in_progress",
        ToolDisplay: null, DurableSequence: null, TransientSequence: 1));

    [Theory]
    [InlineData("Giá mới.")]
    [InlineData("Bảng giá thay thế dài hơn bảng giá cũ và vẫn chỉ hiển thị một lần.")]
    public void Retry_removes_frozen_failed_answer_but_preserves_earlier_prose_and_activity(string replacement)
    {
        var reply = new AgentReplyAccumulator();
        reply.Begin("s", "r");
        var message = new ChatMessageItem { Role = "assistant" };
        const string earlier = "Để em tìm giá vàng.\n";
        reply.TryAppend("r", "search", 1, earlier);
        message.Content = reply.TakeSnapshot()!.Text;
        var frozen = AgentLiveSegmentBuilder.Freeze(message, Activity("fetch"), 0);
        reply.TryAppend("r", "answer", 1, "Bảng giá cũ chưa hoàn chỉnh.");
        message.Content = reply.TakeSnapshot()!.Text;
        Assert.IsType<TextSegment>(message.Segments![^1]).Text = message.Content[frozen..];
        frozen = AgentLiveSegmentBuilder.Freeze(message, Activity("retry"), frozen);

        // The reset and replacement may coalesce before the UI renders again.
        reply.TryReset("r", "answer", 2);
        reply.TryAppend("r", "answer", 3, replacement);
        var snapshot = reply.TakeSnapshot()!;
        Assert.Equal(earlier.Length, snapshot.ResetOffset);
        frozen = AgentLiveSegmentBuilder.Reset(message, snapshot.ResetOffset!.Value, frozen);
        message.Content = snapshot.Text;
        Assert.IsType<TextSegment>(message.Segments[^1]).Text = snapshot.Text[frozen..];

        Assert.Equal(earlier + replacement, string.Concat(message.Segments.OfType<TextSegment>().Select(text => text.Text)));
        Assert.Equal(new[] { "fetch", "retry" }, message.Segments.OfType<ActivitySegment>().Select(item => item.ActivityId));
        Assert.Equal(earlier.Length, frozen);
        Assert.False(reply.TryReset("r", "answer", 2));
        Assert.Null(reply.TakeSnapshot());
    }

    [Fact]
    public void Retry_clears_the_existing_live_tail_before_new_text_arrives()
    {
        var message = new ChatMessageItem { Content = "answer" };
        AgentLiveSegmentBuilder.Freeze(message, Activity("retry"), 0);
        var frozen = AgentLiveSegmentBuilder.Reset(message, 0, message.Content.Length);
        Assert.Equal(0, frozen);
        Assert.All(message.Segments!.OfType<TextSegment>(), text => Assert.Empty(text.Text));
        Assert.Single(message.Segments!.OfType<ActivitySegment>());
    }

    [Theory]
    [InlineData("Bắt đầu với câu đầu tiên để kiểm tra phần hiển thị lựa chọn:")]
    [InlineData("Câu 3: Anh muốn em tập trung hỏi về chủ đề nào trong các câu còn lại ạ?")]
    [InlineData("Em chào anh.\nBắt đầu với câu hỏi đầu tiên:")]
    [InlineData("**Câu hỏi**: Anh chọn `C#` hay Python?")]
    public void Activity_follows_whole_received_prose_with_no_orphaned_suffix(string prose)
    {
        var message = new ChatMessageItem { Role = "assistant", Content = prose };
        var boundary = AgentLiveSegmentBuilder.Freeze(message, Activity("question"), 0);
        Assert.Equal(prose.Length, boundary);
        Assert.Equal(prose, Assert.IsType<TextSegment>(message.Segments![0]).Text);
        Assert.False(Assert.IsType<TextSegment>(message.Segments[0]).IsLive);
        Assert.IsType<ActivitySegment>(message.Segments[1]);
        Assert.Empty(Assert.IsType<TextSegment>(message.Segments[2]).Text);
    }

    [Fact]
    public void Pending_final_deltas_are_frozen_before_activity_and_next_step_remains_live()
    {
        var accumulator = new AgentReplyAccumulator();
        accumulator.Begin("session", "run");
        var callbacks = new List<Action>();
        var message = new ChatMessageItem { Role = "assistant" };
        int frozen = 0;
        var scheduler = new AgentReplyRenderScheduler(accumulator, callbacks.Add, snapshot =>
        {
            message.Content = snapshot.Text;
            if (message.Segments?.LastOrDefault() is TextSegment tail)
                tail.Text = snapshot.Text[frozen..];
        });
        accumulator.TryAppend("run", "step1", 1, "Anh chọn lựa ");
        scheduler.Schedule();
        callbacks[0]();
        accumulator.TryAppend("run", "step1", 2, "chọn nào ạ?");
        scheduler.Schedule();
        // Activity arrives before the scheduled text render.
        scheduler.ApplyPending();
        frozen = AgentLiveSegmentBuilder.Freeze(message, Activity("question"), frozen);
        Assert.Equal("Anh chọn lựa chọn nào ạ?", Assert.IsType<TextSegment>(message.Segments![0]).Text);
        callbacks[1]();
        Assert.Empty(Assert.IsType<TextSegment>(message.Segments[2]).Text);

        accumulator.TryAppend("run", "step2", 1, "Được anh. Câu tiếp theo:");
        scheduler.Schedule();
        callbacks[2]();
        frozen = AgentLiveSegmentBuilder.Freeze(message, Activity("next"), frozen);
        Assert.Equal("Được anh. Câu tiếp theo:", Assert.IsType<TextSegment>(message.Segments[2]).Text);
        Assert.False(Assert.IsType<TextSegment>(message.Segments[2]).IsLive);
        Assert.Equal(message.Content, string.Concat(message.Segments.OfType<TextSegment>().Select(text => text.Text)));
        Assert.Equal(message.Content.Length, frozen);
    }
}
