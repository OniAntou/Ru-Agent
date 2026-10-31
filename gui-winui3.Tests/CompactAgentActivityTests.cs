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
        Assert.Equal("Ran commands", summary.Title);
        Assert.Equal("waiting for you · 1 failed", summary.Meta);
        Assert.Equal(AgentActivitySummaryTone.Failed, summary.Tone);
        Assert.Equal(AgentActivitySummaryProjection.FailedGlyph, summary.Glyph);
        Assert.False(summary.IsLive);
    }

    [Fact]
    public void A_finished_turn_reads_as_its_three_most_frequent_kinds_of_work()
    {
        var message = new ChatMessageItem
        {
            Role = "assistant", AgentActivities = new()
            {
                Activity("t", AgentActivityKind.Thinking, AgentActivityState.Completed),
                Activity("r1", AgentActivityKind.Explore, AgentActivityState.Completed),
                Activity("s1", AgentActivityKind.Search, AgentActivityState.Completed),
                Activity("e1", AgentActivityKind.Edit, AgentActivityState.Completed),
                Activity("e2", AgentActivityKind.Edit, AgentActivityState.Completed),
                Activity("c1", AgentActivityKind.Terminal, AgentActivityState.Completed),
                Activity("c2", AgentActivityKind.Terminal, AgentActivityState.Completed),
            }
        };
        var summary = AgentActivitySummaryProjection.ForMessage(message);
        // Ties keep first appearance; thinking describes the run, not work done.
        Assert.Equal("Edited files, ran commands, read files and more", summary.Title);
        Assert.Equal(string.Empty, summary.Meta);
        Assert.Equal(AgentActivitySummaryTone.Quiet, summary.Tone);
        Assert.Equal(AgentActivitySummaryProjection.KindGlyph(AgentActivityKind.Edit), summary.Glyph);
        Assert.Equal(7, summary.Count);

        message.AgentActivities.Remove(message.AgentActivities.Single(item => item.ActivityId == "s1"));
        Assert.Equal("Edited files, ran commands and read files", AgentActivitySummaryProjection.ForMessage(message).Title);
    }

    [Theory]
    [InlineData(new[] { AgentActivityKind.Search }, "Searched code")]
    [InlineData(new[] { AgentActivityKind.Explore, AgentActivityKind.Search }, "Read files and searched code")]
    [InlineData(new AgentActivityKind[0], "Thought it through")]
    public void Done_titles_join_like_a_sentence(AgentActivityKind[] order, string expected)
    {
        Assert.Equal(expected, AgentActivitySummaryProjection.DoneTitle(order));
    }

    [Theory]
    [InlineData(0, 34, "0:34")]
    [InlineData(1, 2, "1:02")]
    [InlineData(65, 5, "1:05:05")]
    public void Elapsed_time_reads_as_a_clock(int minutes, int seconds, string expected)
    {
        Assert.Equal(expected, AgentActivitySummaryProjection.Elapsed(TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Summary_updates_for_live_completion_and_does_not_claim_running_after_the_turn_ends()
    {
        var frame = Frame("tool", AgentActivityKind.Terminal, AgentActivityState.Started);
        var item = new AgentActivityItem(frame);
        var message = new ChatMessageItem { Role = "assistant", IsStreaming = true, AgentActivities = new() { item } };
        var live = AgentActivitySummaryProjection.ForMessage(message);
        Assert.True(live.IsLive);
        Assert.Equal(item.DisplayText, live.Title);
        Assert.Equal(AgentActivitySummaryProjection.KindGlyph(AgentActivityKind.Terminal), live.Glyph);
        item.UpdateFrom(frame with { State = AgentActivityState.Failed });
        message.IsStreaming = false;
        var summary = AgentActivitySummaryProjection.ForMessage(message);
        Assert.Equal(1, summary.Failed);
        Assert.False(summary.IsLive);
        Assert.Equal("Ran commands", summary.Title);
        message.AgentActivities.Clear();
        Assert.Equal(AgentActivitySummary.Empty, AgentActivitySummaryProjection.ForMessage(message));
    }

    [Fact]
    public void Prose_gets_room_only_on_the_side_where_an_activity_row_sits()
    {
        var lead = new TextSegment("I will look.");
        var activity = Activity("read", AgentActivityKind.Explore, AgentActivityState.Completed);
        var reply = new TextSegment("Found it.");
        var message = new ChatMessageItem
        {
            Role = "assistant",
            Segments = new() { lead, new ActivitySegment(activity.ActivityId, activity), reply },
            AgentActivities = new() { activity },
        };

        Assert.Equal(new Microsoft.UI.Xaml.Thickness(0, 0, 0, 12), lead.ActivityGap);
        Assert.Equal(new Microsoft.UI.Xaml.Thickness(0, 12, 0, 0), reply.ActivityGap);

        // Collapsed compact turns show prose only, so nothing needs room.
        message.CompactAgentActivity = true;
        Assert.Equal(default, lead.ActivityGap);
        Assert.Equal(default, reply.ActivityGap);

        message.IsActivityDetailsExpanded = true;
        Assert.Equal(new Microsoft.UI.Xaml.Thickness(0, 12, 0, 0), reply.ActivityGap);
    }

    [Fact]
    public void An_opened_session_gets_the_preference_although_its_history_arrives_as_a_reset()
    {
        // ReplaceAll raises one Reset without NewItems; applying only NewItems left every
        // message of an opened session in detailed mode.
        var body = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "OnMessagesChanged");
        Assert.Contains("NotifyCollectionChangedAction.Reset) RefreshActivityDisplay()", body);
    }

    [Fact]
    public void The_line_is_quiet_and_its_glyph_gives_way_to_the_chevron()
    {
        var control = GuiSource.Xml("Controls", "AgentActivitySummaryControl.xaml");
        GuiSource.Require(control, "LeadingIcon");
        Assert.Equal("0", GuiSource.Require(control, "ChevronIcon").Attribute("Opacity")?.Value);
        Assert.Equal("Transparent", GuiSource.Require(control, "SummaryButton").Attribute("Background")?.Value);
        Assert.Contains("RuMonoFontFamily", GuiSource.Text("Controls", "AgentActivitySummaryControl.xaml.cs"));
        var code = GuiSource.Text("Controls", "AgentActivitySummaryControl.xaml.cs");
        Assert.Contains("UiTiming.AnimationsEnabled", code);
        Assert.Contains("TitleMinimumMs", code);
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
        // Two named styles rather than an on/off switch.
        Assert.False(GuiSource.NamedElements(settings).ContainsKey("CompactAgentActivityToggle"));
        var verbose = GuiSource.Require(settings, "VerboseActivityOption");
        var compact = GuiSource.Require(settings, "CompactActivityOption");
        Assert.Equal("Verbose", verbose.Attribute("Content")?.Value);
        Assert.Equal("Compact", compact.Attribute("Content")?.Value);
        Assert.Same(verbose.Parent, compact.Parent);
        var code = GuiSource.Text("Views", "SettingsView.ActivityStyle.cs");
        Assert.Contains("ActivityFeedPreferences.Compact = style == \"compact\"", code);
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
