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
        // The panel owns visibility now: an empty or finished plan clears it, and a
        // fresh live plan shows it through the shared motion helper.
        string code = GuiSource.ViewText("ChatView");

        string update = GuiSource.MethodBody(code, "UpdateTodoPanel");
        Assert.Contains("!state.IsVisible", update, StringComparison.Ordinal);
        Assert.Contains("ClearTodoPanel", update, StringComparison.Ordinal);
        Assert.Contains("UiMotion.SetVisible(TodoPanel", update, StringComparison.Ordinal);
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

        string header = GuiSource.MethodBody(code, "UpdateTodoHeader");
        Assert.Contains("TodoPanelNext.Visibility", header, StringComparison.Ordinal);
        Assert.Contains("listOpen &&", header, StringComparison.Ordinal);

        string list = GuiSource.MethodBody(code, "ApplyTodoListState");
        Assert.Contains("TodoPanelList.Visibility", list, StringComparison.Ordinal);
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
        string code = GuiSource.ViewText("ChatView");
        string mark = GuiSource.MethodBody(code, "ApplyTodoMark");
        Assert.Contains("SetMarkOpacity(TodoPanelMarkDot", mark, StringComparison.Ordinal);
        Assert.Contains("SetMarkOpacity(TodoPanelMarkRing", mark, StringComparison.Ordinal);
        Assert.DoesNotContain("TodoPanelMarkCheck", mark, StringComparison.Ordinal);

        string setMark = GuiSource.MethodBody(code, "SetMarkOpacity");
        Assert.Contains("UiMotion.FadeOpacity", setMark, StringComparison.Ordinal);

        var active = RuApp.Services.AgentTodoPanelPresentation.Create(
            [new("Working", TodoItemModel.InProgress), new("Later", TodoItemModel.Pending)], 800);
        var waiting = RuApp.Services.AgentTodoPanelPresentation.Create(
            [new("Done", TodoItemModel.Completed), new("Later", TodoItemModel.Pending)], 800);
        Assert.True(active.HasActive);
        Assert.False(waiting.HasActive);
        Assert.Equal("Later", waiting.Current);
    }

    [Fact]
    public void The_header_shows_the_count_the_next_line_and_the_progress_strip()
    {
        string code = GuiSource.ViewText("ChatView");

        string update = GuiSource.MethodBody(code, "UpdateTodoPanel");
        Assert.Contains("UpdateTodoHeader", update, StringComparison.Ordinal);
        Assert.Contains("UpdateTodoCount", update, StringComparison.Ordinal);
        Assert.Contains("ApplyTodoProgress", update, StringComparison.Ordinal);

        string header = GuiSource.MethodBody(code, "UpdateTodoHeader");
        Assert.Contains("TodoPanelNext.Text", header, StringComparison.Ordinal);
        string count = GuiSource.MethodBody(code, "UpdateTodoCount");
        Assert.Contains("TodoPanelCount.Text", count, StringComparison.Ordinal);
    }

    [Fact]
    public void The_list_drawer_animates_with_reduced_motion_guard()
    {
        // The drawer grows and fades instead of snapping; a reader with animations
        // off gets the final height immediately.
        string code = GuiSource.ViewText("ChatView");
        string list = GuiSource.MethodBody(code, "ApplyTodoListState");

        Assert.Contains("UiTiming.AnimationsEnabled", list, StringComparison.Ordinal);
        Assert.Contains("UiTiming.TodoListOpen", list, StringComparison.Ordinal);
        Assert.Contains("UiTiming.TodoListClose", list, StringComparison.Ordinal);
        Assert.Contains("TodoPanelScroll.Height", list, StringComparison.Ordinal);
        Assert.Contains("EnableDependentAnimation", list, StringComparison.Ordinal);
    }

    [Fact]
    public void Segments_are_reused_and_cross_faded()
    {
        // Rebuilding the strip every update would reset its colours and kill the
        // transition; the same borders are recoloured, and the active one breathes.
        string code = GuiSource.ViewText("ChatView");
        string progress = GuiSource.MethodBody(code, "ApplyTodoProgress");

        Assert.Contains("_todoSegmentVisuals", progress, StringComparison.Ordinal);
        Assert.Contains("UiMotion.FadeBrush", progress, StringComparison.Ordinal);
        Assert.Contains("StartTodoSegmentPulse", progress, StringComparison.Ordinal);

        string pulse = GuiSource.MethodBody(code, "StartTodoSegmentPulse");
        Assert.Contains("UiTiming.AnimationsEnabled", pulse, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_transition_in_place_when_the_plan_advances()
    {
        // The checklist reconciles rows by content so the active tint and marks can
        // cross-fade; a genuinely different plan rebuilds once.
        string control = GuiSource.Text("Controls", "TodoChecklistControl.cs");
        string render = GuiSource.MethodBody(control, "Render");
        Assert.Contains("ContentsMatch", render, StringComparison.Ordinal);

        string apply = GuiSource.MethodBody(control, "ApplyState");
        Assert.Contains("SetForeground", apply, StringComparison.Ordinal);
        Assert.Contains("SetBackground", apply, StringComparison.Ordinal);
        Assert.Contains("SetOpacity", apply, StringComparison.Ordinal);
        Assert.Contains("PopDot", apply, StringComparison.Ordinal);

        Assert.Contains("UiMotion.FadeBrush", GuiSource.MethodBody(control, "SetBackground"), StringComparison.Ordinal);
        Assert.Contains("UiMotion.FadeOpacity", GuiSource.MethodBody(control, "SetOpacity"), StringComparison.Ordinal);
        Assert.Contains("UiTiming.AnimationsEnabled", GuiSource.MethodBody(control, "PopDot"), StringComparison.Ordinal);
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
    public void A_finished_plan_hides_the_shelf_immediately()
    {
        // The shelf is for a live plan: when the last task completes it clears in the
        // same update rather than keeping a completion summary.
        var state = RuApp.Services.AgentTodoPanelPresentation.Create(
            [new("Done", TodoItemModel.Completed)], 800);
        Assert.False(state.IsVisible);

        string code = GuiSource.ViewText("ChatView");
        string update = GuiSource.MethodBody(code, "UpdateTodoPanel");
        Assert.Contains("!state.IsVisible", update, StringComparison.Ordinal);
        Assert.Contains("ClearTodoPanel", update, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearCompletedTodoShelf", code, StringComparison.Ordinal);
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
