using System.Collections.ObjectModel;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class TurnChangesProjectionTests
{
    [Fact]
    public void Only_completed_edits_with_a_file_identity_are_counted()
    {
        var message = Message(Edit("ok", "a.cs", 3, 1),
            Edit("failed", "b.cs", 20, 20, AgentActivityState.Failed),
            Edit("running", "c.cs", 20, 20, AgentActivityState.Started),
            Edit("missing", "", 20, 20),
            new AgentActivityItem(Wire("read", "d.cs", 20, 20) with { Kind = AgentActivityKind.Explore }));
        var summary = TurnChangesProjection.ForMessage(message);
        Assert.Equal("Edited 1 file", summary.Title);
        Assert.Equal("a.cs", Assert.Single(summary.Files).Path);
        Assert.Equal(3, summary.Added);
        Assert.Equal(1, summary.Removed);
    }

    [Fact]
    public void Repeated_files_are_grouped_and_replayed_activities_are_counted_once()
    {
        var summary = TurnChangesProjection.ForMessage(Message(
            Edit("one", @"src\A.cs", 2, 1), Edit("one", "src/A.cs", 5, 3),
            Edit("two", "SRC/a.cs", 4, 0), Edit("three", "other.cs", 1, 1)));
        Assert.Equal(2, summary.Files.Count);
        Assert.Equal(9, summary.Files[0].Added);
        Assert.Equal(3, summary.Files[0].Removed);
        Assert.Equal(2, summary.Files[0].Edits.Count);
        Assert.Equal(10, summary.Added);
        Assert.Equal(4, summary.Removed);
    }

    [Fact]
    public void The_latest_failed_activity_does_not_leave_an_old_success_in_totals()
    {
        var summary = TurnChangesProjection.ForMessage(Message(Edit("same", "a.cs", 3, 1),
            Edit("same", "a.cs", 3, 1, AgentActivityState.Failed)));
        Assert.Empty(summary.Files);
    }

    [Fact]
    public void Streaming_and_non_assistant_messages_have_no_summary()
    {
        var message = Message(Edit("one", "a.cs", 1, 0));
        message.IsStreaming = true;
        Assert.Empty(TurnChangesProjection.ForMessage(message).Files);
        message.IsStreaming = false;
        message.Role = "user";
        Assert.Empty(TurnChangesProjection.ForMessage(message).Files);
    }

    [Fact]
    public void Turns_keep_their_own_totals_after_history_replay_and_later_edits()
    {
        var first = Message(Edit("old", "a.cs", 2, 1));
        first.SourceRunId = "run-one";
        var second = Message(Edit("new", "a.cs", 8, 4));
        second.SourceRunId = "run-two";
        Assert.Equal(2, TurnChangesProjection.ForMessage(first).Added);
        Assert.Equal(8, TurnChangesProjection.ForMessage(second).Added);
    }

    [Fact]
    public void Missing_line_counts_remain_unknown_instead_of_becoming_zero()
    {
        var summary = TurnChangesProjection.ForMessage(Message(Edit("one", "a.cs", null, 0),
            Edit("two", "a.cs", 2, 0)));
        Assert.Null(summary.Added);
        Assert.Equal("—", summary.AddedText);
        Assert.Null(summary.Files[0].Added);
        Assert.Equal("−0", summary.RemovedText);
    }

    [Fact]
    public void Totals_do_not_overflow_an_int_in_large_turns()
    {
        var summary = TurnChangesProjection.ForMessage(Message(Edit("one", "a.cs", int.MaxValue, 0),
            Edit("two", "b.cs", int.MaxValue, 0)));
        Assert.Equal(2L * int.MaxValue, summary.Added);
    }

    [Fact]
    public void Multiple_hunks_keep_their_own_before_after_and_line_numbers()
    {
        var first = new AgentActivityItem(Wire("one", "a.cs", 1, 1) with
        {
            LineStart = 7, Detail = AgentEditPreviewCodec.Encode("old one", "new one"),
        });
        var second = new AgentActivityItem(Wire("two", "a.cs", 1, 1) with
        {
            LineStart = 42, Detail = AgentEditPreviewCodec.Encode("old two", "new two"),
        });
        var file = Assert.Single(TurnChangesProjection.ForMessage(Message(first, second)).Files);
        Assert.Equal(2, file.Edits.Count);
        Assert.Equal("old one", file.Edits[0].BeforeText);
        Assert.Equal("new two", file.Edits[1].AfterText);
        Assert.Equal(42, file.Edits[1].StartLine);
        Assert.True(file.Edits[0].HasPreview);
        Assert.False(Assert.Single(TurnChangesProjection.ForMessage(Message(Edit("no-preview", "b.cs", 1, 0))).Files).Edits[0].HasPreview);
    }

    [Fact]
    public void A_summary_is_a_snapshot_of_recorded_edits_not_current_filesystem_content()
    {
        var edit = Edit("one", "a.cs", 1, 0);
        var message = Message(edit);
        var before = TurnChangesProjection.ForMessage(message);
        edit.UpdateFrom(Wire("one", "a.cs", 9, 0));
        Assert.Equal(1, before.Added);
        Assert.Equal(9, TurnChangesProjection.ForMessage(message).Added);
    }

    [Fact]
    public void Rebuilt_transcript_restores_each_turn_summary_by_run_identity()
    {
        var rows = SessionTranscriptBuilder.Build(
            [new("user", "first", "one"), new("assistant", "done first", "one"),
             new("user", "second", "two"), new("assistant", "done second", "two")],
            [new SessionRunReplay("one", [Edit("one-edit", "first.cs", 2, 1)], []),
             new SessionRunReplay("two", [Edit("two-edit", "second.cs", 7, 3)], [])]);
        var summaries = rows.Where(row => row.Role == "assistant")
            .Select(TurnChangesProjection.ForMessage).ToArray();
        Assert.Equal("first.cs", Assert.Single(summaries[0].Files).Path);
        Assert.Equal(2, summaries[0].Added);
        Assert.Equal("second.cs", Assert.Single(summaries[1].Files).Path);
        Assert.Equal(7, summaries[1].Added);
    }

    [Fact]
    public void The_card_bounds_its_file_list_and_releases_native_subscriptions()
    {
        var card = GuiSource.Xml("Controls", "TurnChangesSummaryControl.xaml");
        Assert.Equal("ListView", GuiSource.Require(card, "FilesList").Name.LocalName);
        Assert.Equal("320", (string?)GuiSource.Require(card, "FilesList").Attribute("MaxHeight"));
        var code = GuiSource.Text("Controls", "TurnChangesSummaryControl.xaml.cs");
        var cleanup = GuiSource.MethodBody(code, "StopObserving");
        Assert.Contains("PropertyChanged -=", cleanup, StringComparison.Ordinal);
        Assert.Contains("CollectionChanged -=", cleanup, StringComparison.Ordinal);
        var navigation = GuiSource.Text("Controls", "TurnChangesSummaryControl.Details.cs");
        Assert.DoesNotContain("ContentDialog", navigation, StringComparison.Ordinal);
        Assert.Contains("DetailsRequested?.Invoke", GuiSource.MethodBody(navigation, "RequestDetails"), StringComparison.Ordinal);
    }

    [Fact]
    public void Turn_cards_navigate_to_the_existing_changes_list_without_a_separate_reader()
    {
        var transcript = GuiSource.Xml("Views", "ChatView.xaml");
        var card = Assert.Single(transcript.Descendants(), element => element.Name.LocalName == "TurnChangesSummaryControl");
        Assert.Equal("OnTurnChangesRequested", (string?)card.Attribute("DetailsRequested"));

        var panel = GuiSource.Xml("Controls", "AgentSidePanelControl.xaml");
        var changes = GuiSource.Require(panel, "ChangesBody");
        Assert.Contains(changes, GuiSource.Require(panel, "ChangesList").Ancestors());
        Assert.DoesNotContain(changes.Descendants(), element => element.Name.LocalName is "ComboBox" or "TurnChangesDetailsControl");
        Assert.DoesNotContain(changes.Descendants(), element => (string?)element.Attribute("Click") == "OnSessionChangesClick");

        var view = GuiSource.ViewText("ChatView");
        var open = GuiSource.MethodBody(view, "OnTurnChangesRequested");
        Assert.Contains("Messages.Contains(owner)", open, StringComparison.Ordinal);
        Assert.Contains("owner.SessionId, _activeSessionId", open, StringComparison.Ordinal);
        Assert.Contains("TurnChangesProjection.ForMessage(owner)", open, StringComparison.Ordinal);
        Assert.Contains("OpenSidePanelTab(SidePanelTabKind.Changes)", open, StringComparison.Ordinal);
        Assert.Contains("SidePanel.RevealChange(_focusedChangeActivityId)", open, StringComparison.Ordinal);
        Assert.Contains("ClearChangeNavigation", GuiSource.MethodBody(view, "ResetAgentState"), StringComparison.Ordinal);
        Assert.Contains("ClearChangeNavigation", GuiSource.MethodBody(view, "Dispose"), StringComparison.Ordinal);
        Assert.Contains("IsTurnChangesOverlay()", GuiSource.MethodBody(view, "IsSidePanelOverlay"), StringComparison.Ordinal);
    }

    [Fact]
    public void Transcript_places_the_card_after_reply_text_and_before_message_actions()
    {
        var doc = GuiSource.Xml("Views", "ChatView.xaml");
        var reply = GuiSource.Require(doc, "AgentReplyTextBlock");
        var children = reply.Parent!.Elements().ToArray();
        int textIndex = Array.IndexOf(children, reply);
        Assert.Equal("TurnChangesSummaryControl", children[textIndex + 1].Name.LocalName);
        Assert.Equal("{x:Bind}", (string?)children[textIndex + 1].Attribute("Message"));
    }

    private static ChatMessageItem Message(params AgentActivityItem[] edits) => new()
    {
        Role = "assistant", AgentActivities = new ObservableCollection<AgentActivityItem>(edits),
    };

    private static AgentActivityItem Edit(string id, string path, int? added, int? removed,
        AgentActivityState state = AgentActivityState.Completed) => new(Wire(id, path, added, removed) with { State = state });

    private static AgentActivityModel Wire(string id, string path, int? added, int? removed) => new(
        id, AgentActivityKind.Edit, AgentActivityState.Completed, path, null, null, null,
        null, added, removed, null, "succeeded", null, null, null);
}
