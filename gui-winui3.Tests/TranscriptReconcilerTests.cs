using System.Collections.ObjectModel;
using System.Collections.Specialized;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class TranscriptReconcilerTests
{
    private static AgentActivityItem Activity(string id, AgentActivityState state = AgentActivityState.Completed) => new(
        new AgentActivityModel(id, AgentActivityKind.Thinking, state,
            null, null, null, null, null, null, null, 4000, "succeeded", null, null, null));

    private static ChatMessageItem Row(string role, string content, int? index = null, string? run = null) =>
        new() { Role = role, Content = content, SourceMessageIndex = index, SourceRunId = run };

    private static (BatchObservableCollection<ChatMessageItem> Items, List<NotifyCollectionChangedAction> Changes) Bound(
        params ChatMessageItem[] rows)
    {
        var items = new BatchObservableCollection<ChatMessageItem>();
        foreach (var row in rows) items.Add(row);
        var changes = new List<NotifyCollectionChangedAction>();
        items.CollectionChanged += (_, args) => changes.Add(args.Action);
        return (items, changes);
    }

    [Fact]
    public void Unchanged_reload_keeps_every_row_without_a_reset()
    {
        var question = Row("user", "Xin chào", 0, "r1");
        var answer = Row("assistant", "Chào anh", 1, "r1");
        var (items, changes) = Bound(question, answer);

        var result = TranscriptReconciler.Apply(items, [Row("user", "Xin chào", 0, "r1"), Row("assistant", "Chào anh", 1, "r1")]);

        Assert.Same(question, items[0]);
        Assert.Same(answer, items[1]);
        Assert.Empty(changes);
        Assert.Equal(2, result.Kept);
    }

    [Fact]
    public void Only_a_changed_row_is_replaced_and_new_rows_are_appended()
    {
        var first = Row("user", "one", 0);
        var stale = Row("assistant", "draft", 1);
        var (items, changes) = Bound(first, stale);
        var fresh = Row("assistant", "final", 1);
        var appended = Row("user", "two", 2);

        var result = TranscriptReconciler.Apply(items, [Row("user", "one", 0), fresh, appended]);

        Assert.Same(first, items[0]);
        Assert.Same(fresh, items[1]);
        Assert.Same(appended, items[2]);
        Assert.Equal([NotifyCollectionChangedAction.Replace, NotifyCollectionChangedAction.Add], changes);
        Assert.Equal((1, 1, 1, 0), (result.Kept, result.Replaced, result.Added, result.Removed));
    }

    [Fact]
    public void A_shorter_history_trims_the_tail()
    {
        var kept = Row("user", "one", 0);
        var (items, changes) = Bound(kept, Row("assistant", "a", 1), Row("user", "two", 2));

        var result = TranscriptReconciler.Apply(items, [Row("user", "one", 0)]);

        Assert.Same(kept, Assert.Single(items));
        Assert.Equal([NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Remove], changes);
        Assert.Equal(2, result.Removed);
    }

    [Fact]
    public void A_row_sent_locally_adopts_its_durable_identity()
    {
        var sent = Row("user", "Giá vàng hôm nay?");

        var (items, _) = Bound(sent);
        TranscriptReconciler.Apply(items, [Row("user", "Giá vàng hôm nay?", 4, "gold")]);

        Assert.Same(sent, items[0]);
        Assert.Equal(4, sent.SourceMessageIndex);
        Assert.Equal("gold", sent.SourceRunId);
    }

    [Fact]
    public void A_row_naming_another_message_is_replaced_even_with_equal_text()
    {
        var (items, _) = Bound(Row("user", "ok", 2));
        var other = Row("user", "ok", 5);

        TranscriptReconciler.Apply(items, [other]);

        Assert.Same(other, items[0]);
    }

    [Fact]
    public void A_settled_activity_change_replaces_its_row()
    {
        var running = Row("assistant", "x", 1, "r1");
        running.AgentActivities = new([Activity("t", AgentActivityState.Started)]);
        var settled = Row("assistant", "x", 1, "r1");
        settled.AgentActivities = new([Activity("t")]);
        var (items, _) = Bound(running);

        TranscriptReconciler.Apply(items, [settled]);

        Assert.Same(settled, items[0]);
    }

    [Fact]
    public void A_kept_row_keeps_its_usage_when_the_history_has_none()
    {
        var usage = new AgentModelUsage(10, 5, 15, null, null);
        var answer = Row("assistant", "a", 1);
        answer.Usage = usage;
        var (items, _) = Bound(answer);

        TranscriptReconciler.Apply(items, [Row("assistant", "a", 1)]);

        Assert.Same(usage, items[0].Usage);
    }

    [Fact]
    public void The_streamed_reply_stays_with_its_segments_and_leaves_the_shared_feed()
    {
        var thinking = Activity("run-thinking");
        var shared = new ObservableCollection<AgentActivityItem>([thinking]);
        var live = new ChatMessageItem { Role = "assistant", Content = "Xong rồi", IsStreaming = true, AgentActivities = shared };
        var segments = new ObservableCollection<AssistantSegment>([new ActivitySegment(thinking.ActivityId, thinking), new TextSegment("Xong rồi") { IsLive = true }]);
        live.Segments = segments;
        var restored = Row("assistant", "Xong rồi", 3, "run");
        restored.AgentActivities = new([thinking]);
        restored.Segments = new([new TextSegment("rebuilt")]);
        var (items, changes) = Bound(Row("user", "Làm đi", 2, "run"), live);

        var result = TranscriptReconciler.Apply(items, [Row("user", "Làm đi", 2, "run"), restored], live, "run");

        Assert.True(result.KeptLiveReply);
        Assert.Same(live, items[1]);
        Assert.Empty(changes);
        Assert.Same(segments, live.Segments);
        Assert.Same(restored.AgentActivities, live.AgentActivities);
        Assert.NotSame(shared, live.AgentActivities);
        Assert.Equal((3, "run"), (live.SourceMessageIndex, live.SourceRunId));
        Assert.False(live.IsStreaming);
        Assert.False(((TextSegment)segments[1]).IsLive);
    }

    [Fact]
    public void A_streamed_reply_the_history_does_not_hold_is_replaced()
    {
        var live = new ChatMessageItem { Role = "assistant", Content = "{\"tool\":\"read\"}" };
        var restored = Row("assistant", "Here is the file.", 3, "run");
        var (items, _) = Bound(live);

        var result = TranscriptReconciler.Apply(items, [restored], live, "run");

        Assert.False(result.KeptLiveReply);
        Assert.Same(restored, items[0]);
    }
}
