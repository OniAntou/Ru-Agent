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
    public void Compact_disclosure_preserves_the_original_timeline()
    {
        var before = new TextSegment("I will inspect the file.");
        var live = new TextSegment("Result") { IsLive = true, IsError = true };
        var activity = Activity("read", AgentActivityKind.Explore, AgentActivityState.Completed);
        var segments = new ObservableCollection<AssistantSegment> { before, new ActivitySegment(activity.ActivityId, activity), live };
        var message = new ChatMessageItem { Role = "assistant", Segments = segments, AgentActivities = new() { activity } };

        Assert.Same(segments, message.DisplayedSegments);
        message.CompactAgentActivity = true;
        // The narration before the row folds into the summary with the row itself.
        Assert.Equal(new AssistantSegment[] { live }, message.DisplayedSegments);
        Assert.Equal(3, segments.Count);
        Assert.Single(message.AgentActivities!);
        live.Text = "Result still streams";
        Assert.Same(live, message.DisplayedSegments![0]);
        Assert.True(((TextSegment)message.DisplayedSegments[0]).IsLive);
        Assert.True(((TextSegment)message.DisplayedSegments[0]).IsError);

        message.IsActivityDetailsExpanded = true;
        Assert.Same(segments, message.DisplayedSegments);
        message.IsActivityDetailsExpanded = false;
        message.CompactAgentActivity = false;
        Assert.Same(segments, message.DisplayedSegments);
    }

    [Fact]
    public void Live_appends_replacements_and_resets_show_only_the_tail_without_revealing_activity_rows()
    {
        var segments = new ObservableCollection<AssistantSegment>();
        var message = new ChatMessageItem { Role = "assistant", IsStreaming = true, CompactAgentActivity = true, Segments = segments };
        var text = new TextSegment("before");
        var activity = Activity("tool", AgentActivityKind.Terminal, AgentActivityState.Started);
        segments.Add(text);
        Assert.Equal(new AssistantSegment[] { text }, message.DisplayedSegments);
        segments.Add(new ActivitySegment(activity.ActivityId, activity));
        var tail = new TextSegment("after");
        segments.Add(tail);
        Assert.Equal(new AssistantSegment[] { tail }, message.DisplayedSegments);
        var more = new TextSegment(" and more");
        segments.Add(more);
        Assert.Equal(new AssistantSegment[] { tail, more }, message.DisplayedSegments);

        var inserted = new TextSegment("middle");
        segments.Insert(1, inserted);
        Assert.Equal(new AssistantSegment[] { tail, more }, message.DisplayedSegments);
        segments.Remove(text);
        Assert.Equal(new AssistantSegment[] { tail, more }, message.DisplayedSegments);
        segments.Clear();
        Assert.Empty(message.DisplayedSegments!);
        message.Segments = new() { tail };
        Assert.Same(tail, Assert.Single(message.DisplayedSegments!));
        message.Segments = null;
        Assert.Null(message.DisplayedSegments);
    }

    [Fact]
    public void Collapsing_folds_narration_between_rows_instead_of_stacking_it_under_the_summary()
    {
        // Regression: collapsing kept every text slice, so each "now I will…" line
        // written between tool rows piled up below the summary in a block.
        var first = Activity("mkdir", AgentActivityKind.Terminal, AgentActivityState.Completed);
        var second = Activity("test", AgentActivityKind.Terminal, AgentActivityState.Completed);
        var opening = new TextSegment("Python is installed. Creating the folder.");
        var between = new TextSegment("Folder created. Running the tests.");
        var answer = new TextSegment("Done.");
        var segments = new ObservableCollection<AssistantSegment>
        {
            opening,
            new ActivitySegment(first.ActivityId, first),
            between,
            new ActivitySegment(second.ActivityId, second),
            answer,
        };
        var message = new ChatMessageItem
        {
            Role = "assistant", CompactAgentActivity = true, Segments = segments,
            AgentActivities = new() { first, second },
        };

        Assert.Equal(new AssistantSegment[] { answer }, message.DisplayedSegments);

        // Expanding restores the narration in place, between its rows.
        message.IsActivityDetailsExpanded = true;
        Assert.Same(segments, message.DisplayedSegments);
    }

    [Fact]
    public void A_settled_turn_that_ends_on_a_row_still_shows_its_last_prose()
    {
        var plan = Activity("plan", AgentActivityKind.Todo, AgentActivityState.Completed);
        var answer = new TextSegment("All done.");
        var tail = new TextSegment(string.Empty);
        var segments = new ObservableCollection<AssistantSegment>
        {
            answer, new ActivitySegment(plan.ActivityId, plan), tail,
        };
        var message = new ChatMessageItem
        {
            Role = "assistant", IsStreaming = true, CompactAgentActivity = true, Segments = segments,
            AgentActivities = new() { plan },
        };

        // While the turn runs the empty tail is where the next prose will stream.
        Assert.Equal(new AssistantSegment[] { tail }, message.DisplayedSegments);

        message.IsStreaming = false;
        Assert.Equal(new AssistantSegment[] { answer }, message.DisplayedSegments);
    }

    [Fact]
    public void A_finished_verbose_turn_collapses_its_own_process_rows()
    {
        var activity = Activity("read", AgentActivityKind.Explore, AgentActivityState.Completed);
        var segments = new ObservableCollection<AssistantSegment>
        {
            new TextSegment("I inspected the file."),
            new ActivitySegment(activity.ActivityId, activity),
        };
        var message = new ChatMessageItem
        {
            Role = "assistant",
            IsStreaming = true,
            Segments = segments,
            AgentActivities = new() { activity },
        };

        message.Content = "The answer is 42.";
        Assert.False(message.ShowActivitySummary);
        Assert.False(message.ActivityDetailsCollapsed);

        message.IsStreaming = false;

        Assert.True(message.AutoCollapseCompleted);
        Assert.True(message.ShowActivitySummary);
        Assert.True(message.ActivityDetailsCollapsed);
        Assert.DoesNotContain(message.DisplayedSegments!, segment => segment is ActivitySegment);

        message.IsActivityDetailsExpanded = true;
        Assert.False(message.ActivityDetailsCollapsed);
        Assert.Contains(message.DisplayedSegments!, segment => segment is ActivitySegment);
    }

    [Fact]
    public void A_turn_without_an_answer_or_with_a_running_row_stays_verbose()
    {
        var activity = Activity("read", AgentActivityKind.Explore, AgentActivityState.Started);
        var message = new ChatMessageItem
        {
            Role = "assistant",
            IsStreaming = true,
            Content = "Still working.",
            Segments = new() { new ActivitySegment(activity.ActivityId, activity) },
            AgentActivities = new() { activity },
        };

        message.IsStreaming = false;
        Assert.False(message.AutoCollapseCompleted);
        Assert.False(message.ActivityDetailsCollapsed);

        var noAnswer = new ChatMessageItem
        {
            Role = "assistant",
            IsStreaming = false,
            AgentActivities = new() { Activity("read", AgentActivityKind.Explore, AgentActivityState.Completed) },
        };
        Assert.False(noAnswer.AutoCollapseCompleted);
        Assert.False(noAnswer.ActivityDetailsCollapsed);
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
    public void Legacy_replay_collapses_the_finished_turn_and_reveals_the_same_feed_on_disclosure()
    {
        var message = new ChatMessageItem
        {
            Role = "assistant", Content = "The answer", AgentActivities = new()
            {
                Activity("old", AgentActivityKind.Thinking, AgentActivityState.Completed)
            }
        };
        Assert.True(message.ShowActivitySummary);
        Assert.False(message.ShowLegacyActivityFeed);
        Assert.True(message.ShowLegacyText);
        Assert.Equal("The answer", message.Content);
        message.IsActivityDetailsExpanded = true;
        Assert.True(message.ShowLegacyActivityFeed);

        message.IsActivityDetailsExpanded = false;
        message.CompactAgentActivity = true;
        Assert.False(message.ShowLegacyActivityFeed);
    }

    [Fact]
    public void A_live_title_does_not_split_an_astral_character_at_the_cut()
    {
        // 58 'a' + pair + 'b' is 61 units; the 59-unit cut would land between the pair's halves.
        string fileName = new string('a', 58) + "\U0001F600" + "b";
        var activity = new AgentActivityItem(new AgentActivityModel(
            "explore", AgentActivityKind.Explore, AgentActivityState.Started,
            fileName, null, null, null, null, null, null, null, "succeeded", null, null, null));
        var message = new ChatMessageItem
        {
            Role = "assistant", IsStreaming = true, AgentActivities = new() { activity }
        };

        var summary = AgentActivitySummaryProjection.ForMessage(message);

        Assert.Equal("Exploring " + new string('a', 58) + "…", summary.Title);
    }

    [Fact]
    public void A_live_title_names_the_step_in_progress_with_its_row_wording()
    {
        static AgentActivityItem Row(AgentActivityKind kind, string? file = null, string? symbol = null,
            string? tool = null, string? detail = null) => new(new AgentActivityModel(
            "row", kind, AgentActivityState.Started, file, null, null, symbol, null, null, null, null,
            "in_progress", tool, null, null, detail));

        Assert.Equal("Reading ChatView.xaml…",
            AgentActivitySummaryProjection.LiveTitle(Row(AgentActivityKind.Analyze, file: "ChatView.xaml", tool: "View")));
        Assert.Equal("Searching for \"steer\" in gui-winui3/Views…",
            AgentActivitySummaryProjection.LiveTitle(Row(AgentActivityKind.Search, symbol: "steer", detail: "gui-winui3/Views")));
        Assert.Equal("Searching in src…", AgentActivitySummaryProjection.LiveTitle(Row(AgentActivityKind.Search, detail: "src")));
        Assert.Equal("Running dotnet test…", AgentActivitySummaryProjection.LiveTitle(Row(AgentActivityKind.Terminal, detail: "dotnet test")));
        Assert.Equal("Opening github.com…",
            AgentActivitySummaryProjection.LiveTitle(Row(AgentActivityKind.Web, tool: "Browser", detail: "open github.com")));
        Assert.Equal("Waiting for 2 subagents…", AgentActivitySummaryProjection.LiveTitle(new AgentActivityItem(new AgentActivityModel(
            "wait", AgentActivityKind.Status, AgentActivityState.Started, null, null, null, null, 2, null, null, null,
            "waiting_subagents", "Wait for subagent", null, null))));
        Assert.Equal("Thinking…", AgentActivitySummaryProjection.LiveTitle(Row(AgentActivityKind.Thinking)));
        Assert.Equal(AgentActivitySummaryProjection.WorkingTitle, AgentActivitySummaryProjection.LiveTitle(null));
    }

    [Fact]
    public void Summary_reports_failures_and_waiting_without_counting_thinking_or_narration()
    {
        var approval = Activity("approval", AgentActivityKind.Terminal, AgentActivityState.Waiting);
        var message = new ChatMessageItem
        {
            Role = "assistant", IsStreaming = true, AgentActivities = new()
            {
                Activity("thinking", AgentActivityKind.Thinking, AgentActivityState.Completed),
                Activity("failure", AgentActivityKind.Terminal, AgentActivityState.Failed),
                approval,
                Activity("narration", AgentActivityKind.Narration, AgentActivityState.Completed)
            }
        };
        var summary = AgentActivitySummaryProjection.ForMessage(message);
        // Steps are tool actions: thinking and narration describe the run, not work in it.
        Assert.Equal(2, summary.Count);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Waiting);
        Assert.Equal(approval.RowVerb, summary.Title);
        Assert.Equal("waiting for you · 1 failed", summary.Meta);
        Assert.Equal(AgentActivitySummaryTone.Failed, summary.Tone);
        Assert.Equal(AgentActivitySummaryProjection.FailedGlyph, summary.Glyph);
        Assert.False(summary.IsLive);
    }

    [Fact]
    public void A_finished_turn_reads_as_a_counted_recap_of_its_three_most_frequent_kinds_of_work()
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
        Assert.Equal("Edited 2 files, ran 2 commands, listed files and more", summary.Title);
        Assert.Equal(summary.Title, summary.Recap);
        Assert.Equal(string.Empty, summary.Meta);
        Assert.Equal(AgentActivitySummaryTone.Quiet, summary.Tone);
        Assert.Equal(AgentActivitySummaryProjection.KindGlyph(AgentActivityKind.Edit), summary.Glyph);
        Assert.Equal(6, summary.Count);

        message.AgentActivities.Remove(message.AgentActivities.Single(item => item.ActivityId == "s1"));
        Assert.Equal("Edited 2 files, ran 2 commands and listed files", AgentActivitySummaryProjection.ForMessage(message).Title);
    }

    [Fact]
    public void A_recap_counts_files_once_however_often_they_were_opened()
    {
        static AgentActivityItem Read(string id, string file) => new(new AgentActivityModel(
            id, AgentActivityKind.Analyze, AgentActivityState.Completed, file, null, null, null, null, null, null, null,
            "succeeded", "View", null, null));
        var summary = AgentActivitySummaryProjection.ForSteps(
            [Read("a", "App.cs"), Read("b", "App.cs"), Read("c", "Chat.cs"),
             Activity("s1", AgentActivityKind.Search, AgentActivityState.Completed),
             Activity("s2", AgentActivityKind.Search, AgentActivityState.Completed)], live: false);

        Assert.Equal("Read 2 files and searched twice", summary.Recap);
        Assert.Equal("5 steps", summary.StepsText);
    }

    [Theory]
    [InlineData(new[] { "searched" }, "Searched")]
    [InlineData(new[] { "read 2 files", "searched twice" }, "Read 2 files and searched twice")]
    [InlineData(new[] { "a", "b", "c", "d" }, "A, b, c and more")]
    [InlineData(new string[0], "")]
    public void Recaps_join_like_a_sentence(string[] phrases, string expected)
    {
        Assert.Equal(expected, AgentActivitySummaryProjection.Recap(phrases));
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
        Assert.Equal("Running…", live.Title);
        Assert.Equal(AgentActivitySummaryProjection.KindGlyph(AgentActivityKind.Terminal), live.Glyph);
        item.UpdateFrom(frame with { State = AgentActivityState.Failed });
        message.IsStreaming = false;
        var summary = AgentActivitySummaryProjection.ForMessage(message);
        Assert.Equal(1, summary.Failed);
        Assert.False(summary.IsLive);
        Assert.Equal("Ran 1 command", summary.Title);
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

        // Collapsed compact turns show the final prose only, so nothing needs room.
        message.CompactAgentActivity = true;
        Assert.Equal(new AssistantSegment[] { reply }, message.DisplayedSegments);
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
        // The turn's process header is the summary line; its recap tail trims in a narrow column.
        var control = GuiSource.Xml("Controls", "TurnProcessDisclosure.xaml");
        GuiSource.Require(control, "LeadingIcon");
        Assert.Equal("0", GuiSource.Require(control, "ChevronIcon").Attribute("Opacity")?.Value);
        Assert.Equal("Transparent", GuiSource.Require(control, "HeaderButton").Attribute("Background")?.Value);
        Assert.Equal("CharacterEllipsis", GuiSource.Require(control, "MetaText").Attribute("TextTrimming")?.Value);
        Assert.Contains("AnimationsEnabled", GuiSource.Text("Controls", "TurnProcessDisclosure.xaml.cs"));
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
        Assert.Contains(chat.Descendants(), node => node.Name.LocalName == "TurnProcessDisclosure");
        Assert.Contains("{x:Bind DisplayedSegments, Mode=OneWay}", GuiSource.AttributeValues(chat, "ItemsSource"));
        Assert.Contains("AgentActivityTemplate", GuiSource.NamedElements(chat).Keys.Concat(
            chat.Descendants().Attributes(GuiSource.Xaml + "Key").Select(attribute => attribute.Value)));
    }

    private static AgentActivityItem Activity(string id, AgentActivityKind kind, AgentActivityState state) => new(Frame(id, kind, state));

    private static AgentActivityModel Frame(string id, AgentActivityKind kind, AgentActivityState state) =>
        new(id, kind, state, null, null, null, null, null, null, null, null, "succeeded", null, null, null);
}
