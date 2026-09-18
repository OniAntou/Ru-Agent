using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The task list's display: a read-only checklist in the transcript.
/// </summary>
/// <remarks>
/// The validation and the store are asserted in the Broker suite. What is asserted here
/// is what the user sees, and specifically that it offers nothing to click: the agent
/// plans and the user watches, so an editing affordance would be a promise the product
/// does not keep.
/// </remarks>
public sealed class TodoChecklistTests
{
    /// <summary>
    /// The checklist element, found in an already loaded document.
    /// </summary>
    /// <remarks>
    /// Takes the document rather than loading its own. GuiSource.Xml parses a fresh tree
    /// on every call and IsDescendantOf compares by reference, so an element read from one
    /// parse is never a descendant of a template read from another.
    /// </remarks>
    private static System.Xml.Linq.XElement ChecklistElement(System.Xml.Linq.XDocument document) =>
        document.Descendants()
            .Where(element => element.Name.LocalName == "TodoChecklistControl")
            .First();

    [Fact]
    public void The_checklist_is_inside_the_agent_activity_template()
    {
        // An activity row is where a plan belongs; anywhere else it would render once,
        // detached from the work it describes.
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var checklist = ChecklistElement(document);

        var template = document.Descendants().FirstOrDefault(element =>
            (string?)element.Attribute(GuiSource.Xaml + "Key") == "AgentActivityTemplate");

        Assert.NotNull(template);
        Assert.True(GuiSource.IsDescendantOf(checklist, template));
    }

    [Fact]
    public void The_checklist_is_shown_only_when_there_is_a_list()
    {
        var checklist = ChecklistElement(GuiSource.Xml("Views", "ChatView.xaml"));

        Assert.Equal(
            "{x:Bind HasTodos, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}",
            GuiSource.AttributeValue(checklist, "Visibility"));
    }

    [Fact]
    public void The_checklist_offers_nothing_to_click()
    {
        // The whole point of the read-only decision. A Click handler here would be an
        // affordance the product does not implement.
        var checklist = ChecklistElement(GuiSource.Xml("Views", "ChatView.xaml"));

        Assert.Null(GuiSource.AttributeValue(checklist, "Click"));
        Assert.Null(GuiSource.AttributeValue(checklist, "Tapped"));
        Assert.Null(GuiSource.AttributeValue(checklist, "SelectionChanged"));
    }

    [Fact]
    public void The_checklist_is_named_for_a_screen_reader()
    {
        Assert.Equal(
            "Agent task list",
            GuiSource.AttributeValue(ChecklistElement(GuiSource.Xml("Views", "ChatView.xaml")), "Name"));
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
    public void The_checklist_is_rendered_from_the_activity_that_carries_it()
    {
        string code = GuiSource.Text("Views", "ChatView.xaml.cs");

        Assert.True(GuiSource.HasMember(code, "RenderTodoChecklist"));

        string attach = GuiSource.MethodBody(code, "AttachActivityItem");
        Assert.Contains("RenderTodoChecklist", attach, StringComparison.Ordinal);

        // A row without a list must not clear one: only the activity carrying a list
        // owns the checklist, so an unrelated row cannot blank the plan.
        string render = GuiSource.MethodBody(code, "RenderTodoChecklist");
        Assert.Contains("is not { Count: > 0 }", render, StringComparison.Ordinal);
        Assert.DoesNotContain(".Clear()", render, StringComparison.Ordinal);
    }
}
