using System.Collections.ObjectModel;
using System.Xml.Linq;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class CompactAgentActivityTests
{
    [Fact]
    public void Detailed_is_the_default_and_compact_disclosure_preserves_the_original_timeline()
    {
        var before = new TextSegment("I will inspect the file.");
        var live = new TextSegment("Result") { IsLive = true, IsError = true };
        var activity = Activity("read", AgentActivityKind.Explore, AgentActivityState.Completed);
        var segments = new ObservableCollection<AssistantSegment> { before, new ActivitySegment(activity.ActivityId, activity), live };
        var message = new ChatMessageItem { Role = "assistant", Segments = segments, AgentActivities = new() { activity } };

        Assert.Same(segments, message.DisplayedSegments);
        message.CompactAgentActivity = true;
        Assert.Equal(new AssistantSegment[] { before, live }, message.DisplayedSegments);
        Assert.Equal(3, segments.Count);
        Assert.Single(message.AgentActivities!);
        live.Text = "Result still streams";
        Assert.Same(live, message.DisplayedSegments![1]);
        Assert.True(((TextSegment)message.DisplayedSegments[1]).IsLive);
        Assert.True(((TextSegment)message.DisplayedSegments[1]).IsError);

        message.IsActivityDetailsExpanded = true;
        Assert.Same(segments, message.DisplayedSegments);
        message.IsActivityDetailsExpanded = false;
        message.CompactAgentActivity = false;
        Assert.Same(segments, message.DisplayedSegments);
    }

    [Fact]
    public void Live_appends_replacements_and_resets_keep_all_prose_without_revealing_activity_rows()
    {
        var segments = new ObservableCollection<AssistantSegment>();
        var message = new ChatMessageItem { CompactAgentActivity = true, Segments = segments };
        var text = new TextSegment("before");
        var activity = Activity("tool", AgentActivityKind.Terminal, AgentActivityState.Started);
        segments.Add(text);
        segments.Add(new ActivitySegment(activity.ActivityId, activity));
        var tail = new TextSegment("after");
        segments.Add(tail);
        Assert.Equal(new AssistantSegment[] { text, tail }, message.DisplayedSegments);

        var inserted = new TextSegment("middle");
        segments.Insert(1, inserted);
        Assert.Equal(new AssistantSegment[] { text, inserted, tail }, message.DisplayedSegments);
        segments.Remove(text);
        Assert.Equal(new AssistantSegment[] { inserted, tail }, message.DisplayedSegments);
        segments.Clear();
        Assert.Empty(message.DisplayedSegments!);
        message.Segments = new() { tail };
        Assert.Same(tail, Assert.Single(message.DisplayedSegments!));
        message.Segments = null;
        Assert.Null(message.DisplayedSegments);
    }

    [Fact]
    public void An_expanded_turn_stays_expanded_when_new_activity_arrives()
    {
        var message = new ChatMessageItem
        {
            CompactAgentActivity = true, IsActivityDetailsExpanded = true, Segments = new()
        };
        var item = Activity("new", AgentActivityKind.Explore, AgentActivityState.Started);
        message.Segments.Add(new ActivitySegment(item.ActivityId, item));
        Assert.True(message.IsActivityDetailsExpanded);
        Assert.Same(message.Segments, message.DisplayedSegments);
        Assert.IsType<ActivitySegment>(Assert.Single(message.DisplayedSegments!));
    }

    [Fact]
    public void Legacy_replay_keeps_the_reply_and_reveals_the_same_feed_on_disclosure()
    {
        var message = new ChatMessageItem
        {
            Role = "assistant", Content = "The answer", AgentActivities = new()
            {
                Activity("old", AgentActivityKind.Thinking, AgentActivityState.Completed)
            }
        };
        Assert.True(message.ShowLegacyActivityFeed);
        message.CompactAgentActivity = true;
        Assert.False(message.ShowLegacyActivityFeed);
        Assert.True(message.ShowLegacyText);
        Assert.Equal("The answer", message.Content);
        message.IsActivityDetailsExpanded = true;
        Assert.True(message.ShowLegacyActivityFeed);
    }

    [Fact]
    public void Summary_reports_failures_and_waiting_without_counting_duplicate_narration()
    {
        var message = new ChatMessageItem
        {
            Role = "assistant", IsStreaming = true, AgentActivities = new()
            {
                Activity("thinking", AgentActivityKind.Thinking, AgentActivityState.Completed),
                Activity("failure", AgentActivityKind.Terminal, AgentActivityState.Failed),
                Activity("approval", AgentActivityKind.Terminal, AgentActivityState.Waiting),
                Activity("narration", AgentActivityKind.Narration, AgentActivityState.Completed)
            }
        };
        var summary = AgentActivitySummaryProjection.ForMessage(message);
        Assert.Equal(3, summary.Count);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Waiting);
        Assert.StartsWith("Waiting", summary.Label);
        Assert.Contains("1 failed", summary.Label);
        Assert.DoesNotContain("completed", summary.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summary_updates_for_live_completion_and_does_not_claim_running_after_the_turn_ends()
    {
        var frame = Frame("tool", AgentActivityKind.Terminal, AgentActivityState.Started);
        var item = new AgentActivityItem(frame);
        var message = new ChatMessageItem { Role = "assistant", IsStreaming = true, AgentActivities = new() { item } };
        Assert.StartsWith("Working", AgentActivitySummaryProjection.ForMessage(message).Label);
        item.UpdateFrom(frame with { State = AgentActivityState.Failed });
        message.IsStreaming = false;
        var summary = AgentActivitySummaryProjection.ForMessage(message);
        Assert.Equal(1, summary.Failed);
        Assert.DoesNotContain("Working", summary.Label);
        message.AgentActivities.Clear();
        Assert.Equal(AgentActivitySummary.Empty, AgentActivitySummaryProjection.ForMessage(message));
    }

    [Fact]
    public void Narration_only_or_non_assistant_messages_have_no_summary()
    {
        var message = new ChatMessageItem { Role = "assistant", AgentActivities = new()
        {
            Activity("narration", AgentActivityKind.Narration, AgentActivityState.Completed)
        } };
        Assert.Equal(0, AgentActivitySummaryProjection.ForMessage(message).Count);
        message.AgentActivities.Add(Activity("read", AgentActivityKind.Explore, AgentActivityState.Completed));
        message.Role = "user";
        Assert.Equal(0, AgentActivitySummaryProjection.ForMessage(message).Count);
    }

    [Fact]
    public void Settings_and_transcript_bind_the_same_presentation_option_and_keep_existing_detail_templates()
    {
        var settings = GuiSource.Xml("Views", "SettingsView.xaml");
        var toggle = GuiSource.Require(settings, "CompactAgentActivityToggle");
        Assert.Equal("ToggleSwitch", toggle.Name.LocalName);
        Assert.Contains("ActivityFeedPreferences.Compact", GuiSource.AttributeValue(toggle, "IsOn"));
        var chat = GuiSource.Xml("Views", "ChatView.xaml");
        Assert.Contains(chat.Descendants(), node => node.Name.LocalName == "AgentActivitySummaryControl");
        Assert.Contains("{x:Bind DisplayedSegments, Mode=OneWay}", GuiSource.AttributeValues(chat, "ItemsSource"));
        Assert.Contains("AgentActivityTemplate", GuiSource.NamedElements(chat).Keys.Concat(
            chat.Descendants().Attributes(GuiSource.Xaml + "Key").Select(attribute => attribute.Value)));
    }

    private static AgentActivityItem Activity(string id, AgentActivityKind kind, AgentActivityState state) => new(Frame(id, kind, state));

    private static AgentActivityModel Frame(string id, AgentActivityKind kind, AgentActivityState state) =>
        new(id, kind, state, null, null, null, null, null, null, null, null, "succeeded", null, null, null);
}
