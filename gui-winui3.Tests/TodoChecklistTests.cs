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
    [InlineData("pending", "\uE739", "Pending")]
    [InlineData("in_progress", "\uE73A", "In progress")]
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

        Assert.Equal("\uE739", model.MarkGlyph);
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
        Assert.Equal("1", GuiSource.AttributeValue(panel, "Row"));
        Assert.True(GuiSource.IsDescendantOf(list, panel));
    }

    [Fact]
    public void The_collapsed_panel_keeps_only_its_header_line()
    {
        // Collapsed means exactly one header line: the rows hide until expanded.
        string code = GuiSource.ViewText("ChatView");

        string update = GuiSource.MethodBody(code, "UpdateTodoPanel");
        Assert.Contains("TodoPanelList.Visibility", update, StringComparison.Ordinal);
    }

    [Fact]
    public void The_panel_is_fused_with_the_composer_into_one_block()
    {
        // One block, not two cards: no outline ring (depth comes from shadow,
        // per the app card style), a count pill, a progress hairline, and the
        // input's own top edge joining underneath.
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var panel = GuiSource.Require(document, "TodoPanel");

        Assert.Equal("8,0,8,0", GuiSource.AttributeValue(panel, "Margin"));
        Assert.Equal("0", GuiSource.AttributeValue(panel, "BorderThickness"));
        Assert.Equal("4,4,0,0", GuiSource.AttributeValue(panel, "CornerRadius"));
        Assert.Contains("RuRestShadow", GuiSource.AttributeValue(panel, "Shadow"), StringComparison.Ordinal);

        GuiSource.Require(document, "TodoPanelCount");
        GuiSource.Require(document, "TodoPanelProgress");
    }

    [Fact]
    public void The_header_shows_a_count_pill_and_a_progress_hairline()
    {
        string code = GuiSource.ViewText("ChatView");

        string update = GuiSource.MethodBody(code, "UpdateTodoPanel");
        Assert.Contains("TodoPanelCount", update, StringComparison.Ordinal);
        Assert.Contains("TodoPanelProgress", update, StringComparison.Ordinal);
    }

    [Fact]
    public void The_progress_hairline_uses_a_gradient_fill()
    {
        // A flat accent fill reads generic; the hairline blends accent into
        // violet from the app tokens.
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var progress = GuiSource.Require(document, "TodoPanelProgress");

        var gradient = progress.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "LinearGradientBrush");

        Assert.NotNull(gradient);
        Assert.True(
            gradient!.Elements().Count(element => element.Name.LocalName == "GradientStop") >= 2);
    }

    [Fact]
    public void The_panel_disappears_when_every_task_is_done()
    {
        // A finished plan has nothing to watch: the panel hides itself and
        // gives the composer its standalone border back.
        string code = GuiSource.ViewText("ChatView");

        string update = GuiSource.MethodBody(code, "UpdateTodoPanel");
        Assert.Contains("ClearTodoPanel", update, StringComparison.Ordinal);
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
    public void The_finished_count_turns_success_and_the_progress_announces_itself()
    {
        string code = GuiSource.ViewText("ChatView");

        string update = GuiSource.MethodBody(code, "UpdateTodoPanel");
        Assert.Contains("AppSuccessBrush", update, StringComparison.Ordinal);
        Assert.Contains("SetToolTip", update, StringComparison.Ordinal);
        Assert.Contains("Collapse task list", update, StringComparison.Ordinal);
        Assert.Contains("Expand task list", update, StringComparison.Ordinal);
    }

    [Fact]
    public void Showing_or_hiding_the_panel_rejoins_the_input_border()
    {
        string code = GuiSource.ViewText("ChatView");

        string update = GuiSource.MethodBody(code, "UpdateTodoPanel");
        Assert.Contains("FuseInputBorder", update, StringComparison.Ordinal);

        string clear = GuiSource.MethodBody(code, "ClearTodoPanel");
        Assert.Contains("RestoreInputBorder", clear, StringComparison.Ordinal);
    }

    [Fact]
    public void The_panel_always_stays_above_the_composer()
    {
        // Collapsed or expanded, the panel never leaves row 1 above the input:
        // collapsing hides the rows, it does not move the block.
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var panel = GuiSource.Require(document, "TodoPanel");

        Assert.Equal("1", GuiSource.AttributeValue(panel, "Row"));

        string code = GuiSource.ViewText("ChatView");
        string update = GuiSource.MethodBody(code, "UpdateTodoPanel");
        Assert.DoesNotContain("SetRow", update, StringComparison.Ordinal);
    }

    [Fact]
    public void The_panel_is_updated_from_the_activity_that_carries_todos()
    {
        string code = GuiSource.ViewText("ChatView");

        Assert.True(GuiSource.HasMember(code, "UpdateTodoPanel"));

        string attach = GuiSource.MethodBody(code, "AttachActivityItem");
        Assert.Contains("UpdateTodoPanel", attach, StringComparison.Ordinal);
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
