using System.Xml.Linq;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The activity row's layout: every child occupies a row of its own.
/// </summary>
/// <remarks>
/// A child without a Grid.Row lands in row 0, on top of the row's own header. That is not a
/// subtle misalignment -- it draws two pieces of text in the same place, which is what a
/// checklist added later did to the label above it. Nothing fails when it happens, so it is
/// asserted here rather than left to whoever next notices a smudge.
/// </remarks>
public sealed class ActivityRowLayoutTests
{
    private static XElement Template()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        return document.Descendants().FirstOrDefault(element =>
            (string?)element.Attribute(GuiSource.Xaml + "Key") == "AgentActivityTemplate")
            ?? throw new InvalidOperationException("AgentActivityTemplate not found");
    }

    private static XElement RowGrid() =>
        Template().Elements().First(element => element.Name.LocalName == "Grid");

    [Fact]
    public void Every_child_of_the_row_declares_its_own_row()
    {
        var grid = RowGrid();
        var children = grid.Elements()
            .Where(element => element.Name.LocalName != "Grid.RowDefinitions")
            .ToArray();

        Assert.NotEmpty(children);

        foreach (var child in children)
        {
            Assert.True(
                child.Attribute("Grid.Row") is not null,
                $"<{child.Name.LocalName}> has no Grid.Row, so it will draw in row 0 on top of the header.");
        }
    }

    [Fact]
    public void The_row_declares_a_row_for_every_child_that_uses_one()
    {
        var grid = RowGrid();
        int declared = grid.Elements()
            .First(element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .Count();

        int highestUsed = grid.Elements()
            .Select(element => (string?)element.Attribute("Grid.Row"))
            .Where(value => value is not null)
            .Select(value => int.Parse(value!))
            .DefaultIfEmpty(-1)
            .Max();

        Assert.True(
            declared > highestUsed,
            $"row {highestUsed} is used but only {declared} row definitions exist.");
    }

    [Fact]
    public void The_checklist_no_longer_lives_in_the_activity_row()
    {
        // The standing plan moved above the composer into its own panel. A
        // checklist back inside the row would render twice and overlap the
        // header again, so its absence here is asserted, not assumed.
        var grid = RowGrid();

        Assert.DoesNotContain(
            grid.Elements(),
            element => element.Name.LocalName == "TodoChecklistControl");
    }

    [Fact]
    public void The_state_probes_take_no_space()
    {
        // They exist for automation and contract tests. If they ever gained size they would
        // push the row's real content around.
        var grid = RowGrid();
        var holder = grid.Elements().FirstOrDefault(element =>
            element.Elements().Any(child =>
                child.Name.LocalName == "Border"
                && (string?)child.Attribute("Opacity") == "0"));

        Assert.NotNull(holder);
        Assert.NotNull(holder!.Attribute("Grid.Row"));

        foreach (var border in holder.Elements())
        {
            Assert.Equal("0", (string?)border.Attribute("Width"));
            Assert.Equal("0", (string?)border.Attribute("Height"));
        }
    }
}
