using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The task list's display: a read-only standing panel above the composer.
/// </summary>
/// <remarks>
/// The validation and the store are asserted in the Broker suite. What is asserted here
/// is what the user sees, and specifically that it offers nothing to click: the agent
/// plans and the user watches, so an editing affordance would be a promise the product
/// does not keep.
/// </remarks>
public sealed class TodoChecklistTests
{
    [Fact]
    public void The_panel_hides_without_a_list_and_shows_with_one()
    {
        // The panel owns visibility now, not a template binding: empty hides it,
        // a fresh list shows it.
        string code = GuiSource.ViewText("ChatView");

        string update = GuiSource.MethodBody(code, "UpdateTodoPanel");
        Assert.Contains("Visibility.Collapsed", update, StringComparison.Ordinal);
        Assert.Contains("Visibility.Visible", update, StringComparison.Ordinal);
    }

    [Fact]
    public void The_checklist_offers_nothing_to_click()
    {
        // The rows stay read-only: the agent plans and the user watches. Only the
        // expand/collapse toggle may be clicked, and it changes no plan state.
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var list = GuiSource.Require(document, "TodoPanelList");

        Assert.Null(GuiSource.AttributeValue(list, "Click"));
        Assert.Null(GuiSource.AttributeValue(list, "Tapped"));
        Assert.Null(GuiSource.AttributeValue(list, "SelectionChanged"));

        var toggle = GuiSource.Require(document, "TodoPanelToggle");
        Assert.Equal("OnTodoPanelToggleClick", GuiSource.AttributeValue(toggle, "Click"));
    }

    [Fact]
    public void The_checklist_is_named_for_a_screen_reader()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");

        Assert.Equal(
            "Agent task list",
            GuiSource.AttributeValue(GuiSource.Require(document, "TodoPanel"), "Name"));
    }

    [Theory]
    [InlineData("pending", "\uEA3A", "Pending")]
    [InlineData("in_progress", "\uEA3B", "In progress")]
    [InlineData("completed", "\uE73E", "Completed")]
    public void Each_status_has_its_own_mark_and_its_own_words(string status, string glyph, string name)
    {
        var model = new TodoItemModel("Do the thing", status);

        Assert.Equal(glyph, model.MarkGlyph);
        Assert.Equal(name, model.StatusName);
    }

    [Fact]
    public void The_three_statuses_are_distinguishable_by_more_than_colour()
    {
        // Colour alone would leave the list unreadable for someone who cannot separate
        // the accent from the muted tone, so the marks must differ too.
        var marks = new[]
        {
            new TodoItemModel("a", "pending").MarkGlyph,
            new TodoItemModel("b", "in_progress").MarkGlyph,
            new TodoItemModel("c", "completed").MarkGlyph,
        };

        Assert.Equal(3, marks.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void An_unknown_status_still_renders_rather_than_throwing()
    {
        // The wire rejects unknown statuses, so this cannot arrive from the host. It is
        // asserted anyway because a view that throws on unexpected data takes the
        // transcript down with it.
        var model = new TodoItemModel("Do the thing", "something-new");

        Assert.Equal("\uEA3A", model.MarkGlyph);
        Assert.Equal("Pending", model.StatusName);
    }

    [Fact]
    public void The_accessible_name_states_the_status_before_the_task()
    {
        var model = new TodoItemModel("Add the tool", "in_progress");

        Assert.Equal("In progress: Add the tool", model.AccessibleName);
    }

    [Fact]
    public void The_checklist_lives_above_the_composer_in_the_input_container()
    {
        // The standing plan sits above the composer, not inside a bubble: one
        // block, updated in place, in the input container's free row.
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var panel = GuiSource.Require(document, "TodoPanel");
        var list = GuiSource.Require(document, "TodoPanelList");
        var inputContainer = GuiSource.Require(document, "InputContainer");

        Assert.True(GuiSource.IsDescendantOf(panel, inputContainer));
        Assert.Equal("2", GuiSource.AttributeValue(panel, "Row")); // directly above the composer (row 3)
        Assert.True(GuiSource.IsDescendantOf(list, panel));
    }

    [Fact]
    public void The_collapsed_panel_keeps_only_its_header_line()
    {
        // Collapsed means exactly one header line: the rows hide until expanded, and
        // the next-task line is shown only while the list is open.
        string code = GuiSource.ViewText("ChatView");

        string update = GuiSource.MethodBody(code, "UpdateTodoPanel");
        Assert.Contains("TodoPanelList.Visibility", update, StringComparison.Ordinal);
        Assert.Contains("TodoPanelNext.Visibility", update, StringComparison.Ordinal);
        Assert.Contains("listOpen &&", update, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shelf_is_a_tab_tucked_under_the_composer()
    {
        // Redesigned by owner request: a free-floating line with a hairline read as a
        // stray underline. The shelf now shares the composer's surface, is rounded on
        // top only, and tucks its open bottom edge under the composer.
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var panel = GuiSource.Require(document, "TodoPanel");

        Assert.Equal("Border", panel.Name.LocalName);
        Assert.Equal("1,1,1,0", GuiSource.AttributeValue(panel, "BorderThickness"));
        Assert.Equal("12,12,0,0", GuiSource.AttributeValue(panel, "CornerRadius"));
        Assert.Contains("RuSurfaceBrush", GuiSource.AttributeValue(panel, "Background"), StringComparison.Ordinal);
        Assert.StartsWith("14,0,14,-", GuiSource.AttributeValue(panel, "Margin"), StringComparison.Ordinal);

        var toggle = GuiSource.Require(document, "TodoPanelToggle");
        foreach (var name in new[] { "TodoPanelMark", "TodoPanelHeader", "TodoPanelNext", "TodoPanelSegments", "TodoPanelBar", "TodoPanelCount", "TodoPanelToggleIcon" })
            Assert.True(GuiSource.IsDescendantOf(GuiSource.Require(document, name), toggle), name);
        Assert.Equal("74", GuiSource.AttributeValue(GuiSource.Require(document, "TodoPanelBar"), "Width"));
        // The head mark is a drawn dot, not a glyph that reads as a button.
        Assert.Equal("7", GuiSource.AttributeValue(GuiSource.Require(document, "TodoPanelMarkDot"), "Width"));
    }

    [Fact]
    public void The_header_mark_follows_whether_a_task_is_in_progress()
    {
        string mark = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "ApplyTodoMark");
        Assert.Contains("TodoPanelMarkDot.Visibility", mark, StringComparison.Ordinal);
        Assert.Contains("TodoPanelMarkCheck.Visibility", mark, StringComparison.Ordinal);

        var active = RuApp.Services.AgentTodoPanelPresentation.Create(
            [new("Working", TodoItemModel.InProgress), new("Later", TodoItemModel.Pending)], 800);
        var waiting = RuApp.Services.AgentTodoPanelPresentation.Create(
            [new("Done", TodoItemModel.Completed), new("Later", TodoItemModel.Pending)], 800);
        Assert.True(active.HasActive);
        Assert.False(waiting.HasActive);
        Assert.Equal("Later", waiting.Current);

        var finished = RuApp.Services.AgentTodoPanelPresentation.Create(
            [new("Done", TodoItemModel.Completed)], 800);
        Assert.True(finished.AllDone);
    }

    [Fact]
    public void The_header_shows_the_count_the_next_line_and_the_progress_strip()
    {
        string code = GuiSource.ViewText("ChatView");

        string update = GuiSource.MethodBody(code, "UpdateTodoPanel");
        Assert.Contains("TodoPanelCount.Text", update, StringComparison.Ordinal);
        Assert.Contains("TodoPanelNext.Text", update, StringComparison.Ordinal);
        Assert.Contains("ApplyTodoProgress", update, StringComparison.Ordinal);
    }

    [Fact]
    public void Expanded_tasks_scroll_inside_a_bounded_shelf()
    {
        // Long plans scroll without consuming the transcript viewport.
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var scroll = GuiSource.Require(document, "TodoPanelScroll");
        Assert.Equal("Auto", GuiSource.AttributeValue(scroll, "VerticalScrollBarVisibility"));
        Assert.Equal("224", GuiSource.AttributeValue(scroll, "MaxHeight"));
        Assert.True(GuiSource.IsDescendantOf(GuiSource.Require(document, "TodoPanelList"), scroll));
    }

    [Fact]
    public void A_finished_plan_keeps_its_completion_summary_until_the_next_turn()
    {
        // A finished plan is a result, not an empty shelf: it stays visible with the
        // completion sentence, then the next accepted turn clears it.
        var state = RuApp.Services.AgentTodoPanelPresentation.Create(
            [new("Done", TodoItemModel.Completed)], 800);
        Assert.True(state.IsVisible);
        Assert.True(state.AllDone);

        string code = GuiSource.ViewText("ChatView");
        string update = GuiSource.MethodBody(code, "UpdateTodoPanel");
        Assert.Contains("state.AllDone", update, StringComparison.Ordinal);

        string begin = GuiSource.MethodBody(code, "BeginAcceptedAgentTurn");
        Assert.Contains("ClearCompletedTodoShelf", begin, StringComparison.Ordinal);

        string clear = GuiSource.MethodBody(code, "ClearCompletedTodoShelf");
        Assert.Contains("IsCompleted", clear, StringComparison.Ordinal);
        Assert.Contains("ClearTodoPanel", clear, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_carry_an_ordinal_and_no_status_labels()
    {
        // The active row is told by mark, weight, tint and its ordinal; the old
        // "NOW"/"NEXT" text labels were removed by owner request.
        string control = GuiSource.Text("Controls", "TodoChecklistControl.cs");
        string body = GuiSource.MethodBody(control, "BuildRow");

        Assert.Contains("ToString(\"00\"", body, StringComparison.Ordinal);
        Assert.Contains("new Ellipse", body, StringComparison.Ordinal);
        Assert.Contains("activitySize - 3", body, StringComparison.Ordinal);
        Assert.DoesNotContain("NOW", body, StringComparison.Ordinal);
        Assert.DoesNotContain("NEXT", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_count_has_no_pill_and_the_toggle_only_tints_its_icon()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");

        var count = GuiSource.Require(document, "TodoPanelCount");
        Assert.Equal("TextBlock", count.Name.LocalName);

        var toggle = GuiSource.Require(document, "TodoPanelToggle");
        Assert.Contains("TodoToggleButtonStyle", GuiSource.AttributeValue(toggle, "Style"), StringComparison.Ordinal);

        var style = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Style"
            && (string?)element.Attribute(GuiSource.Xaml + "Key") == "TodoToggleButtonStyle");

        Assert.NotNull(style);
        string styleXml = style!.ToString();
        Assert.DoesNotContain("Background", styleXml, StringComparison.Ordinal);
        Assert.Contains("RuAccentBrush", styleXml, StringComparison.Ordinal);
    }

    [Fact]
    public void Progress_and_expand_actions_announce_their_state()
    {
        string code = GuiSource.ViewText("ChatView");

        string update = GuiSource.MethodBody(code, "UpdateTodoPanel");
        Assert.Contains("AutomationProperties.SetName(TodoPanelToggle", update, StringComparison.Ordinal);
        Assert.Contains("ApplyTodoProgress", update, StringComparison.Ordinal);
        Assert.Contains("SetToolTip", update, StringComparison.Ordinal);
        Assert.Contains("Collapse task list", update, StringComparison.Ordinal);
        Assert.Contains("Expand task list", update, StringComparison.Ordinal);
    }

    [Fact]
    public void Showing_or_hiding_tasks_does_not_restyle_the_composer()
    {
        string code = GuiSource.ViewText("ChatView");

        string update = GuiSource.MethodBody(code, "UpdateTodoPanel");
        Assert.DoesNotContain("InputBorder", update, StringComparison.Ordinal);

        string clear = GuiSource.MethodBody(code, "ClearTodoPanel");
        Assert.DoesNotContain("InputBorder", clear, StringComparison.Ordinal);
    }

    [Fact]
    public void The_panel_always_stays_above_the_composer()
    {
        // Collapsed or expanded, the panel never leaves row 2 directly above the input:
        // collapsing hides the rows, it does not move the block.
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var panel = GuiSource.Require(document, "TodoPanel");

        Assert.Equal("2", GuiSource.AttributeValue(panel, "Row"));

        string code = GuiSource.ViewText("ChatView");
        string update = GuiSource.MethodBody(code, "UpdateTodoPanel");
        Assert.DoesNotContain("SetRow", update, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_session_clears_the_panel()
    {
        string code = GuiSource.ViewText("ChatView");

        Assert.True(GuiSource.HasMember(code, "ClearTodoPanel"));

        string reset = GuiSource.MethodBody(code, "ResetAgentState");
        Assert.Contains("ClearTodoPanel", reset, StringComparison.Ordinal);
    }
}
