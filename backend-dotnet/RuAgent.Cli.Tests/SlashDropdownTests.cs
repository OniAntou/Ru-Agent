using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class SlashDropdownTests
{
    private static readonly DropdownItem[] Items =
    [
        new("/model", "Switch or show the active model", "/model [name]"),
        new("/compact", "Compact the context"),
        new("/clear", "Clear the screen"),
    ];

    public SlashDropdownTests()
    {
        TuiPalette.Level = TuiColorLevel.None;
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void Compose_EveryLineFillsExactlyTheWidth(int width)
    {
        var lines = SlashDropdown.Compose(width, "commands", Items, 0, 3, "↑↓ select · enter");

        Assert.All(lines, line => Assert.True(
            TuiText.VisibleWidth(line) <= width,
            $"line exceeds {width}: '{line}'"));
        Assert.Equal(width, TuiText.VisibleWidth(lines[0]));
        Assert.Equal(width, TuiText.VisibleWidth(lines[^1]));
    }

    [Fact]
    public void Dropdown_selected_row_uses_brand_label()
    {
        TuiPalette.Level = TuiColorLevel.TrueColor;
        try
        {
            var rows = SlashDropdown.Compose(60, "select", new[] { new DropdownItem("/help", "help") }, 0, 5, null);
            Assert.Contains(TuiPalette.Code(TuiToken.Brand), rows[1]);
            Assert.All(rows, row => Assert.Equal(60, TuiText.VisibleWidth(row)));
        }
        finally
        {
            TuiPalette.ResetLevel();
        }
    }

    [Fact]
    public void Compose_FramesTheListWithATitleAndCounter()
    {
        var lines = SlashDropdown.Compose(60, "commands", Items, 1, 3, "↑↓ select");

        Assert.Contains("commands", lines[0], StringComparison.Ordinal);
        Assert.Contains("↑↓ select", lines[^1], StringComparison.Ordinal);
        Assert.Contains("2/3", lines[^1], StringComparison.Ordinal);
        Assert.Equal(Items.Length + 2, lines.Count);
    }

    [Fact]
    public void Compose_MarksTheSelectedRowAndDescribesEveryRow()
    {
        var lines = SlashDropdown.Compose(60, "commands", Items, 1, 3, null);

        Assert.Contains("❯ /compact", lines[2], StringComparison.Ordinal);
        Assert.DoesNotContain("❯", lines[1], StringComparison.Ordinal);
        Assert.Contains("Compact the context", lines[2], StringComparison.Ordinal);
        Assert.Contains("/model", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_SaysSoWhenNothingMatches()
    {
        var lines = SlashDropdown.Compose(50, "commands", Array.Empty<DropdownItem>(), 0, 5, null);

        Assert.Contains("no matches", lines[1], StringComparison.Ordinal);
        Assert.Contains("0/0", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_WindowScrollsToKeepTheSelectionVisible()
    {
        var many = Enumerable.Range(0, 20).Select(i => new DropdownItem($"/cmd{i:00}", $"Command {i}")).ToArray();
        var lines = SlashDropdown.Compose(60, "commands", many, 15, 5, null);

        Assert.Contains("❯ /cmd15", lines[3], StringComparison.Ordinal);
        Assert.Equal(5 + 2, lines.Count);
    }

    [Theory]
    [InlineData(3, 0, 5, 0, 3)]
    [InlineData(20, 0, 5, 0, 5)]
    [InlineData(20, 15, 5, 13, 18)]
    [InlineData(20, 10, 5, 8, 13)]
    public void Window_AlwaysContainsTheSelection(int count, int selected, int maxVisible, int expectedStart, int expectedEnd)
    {
        var (start, end) = SlashDropdown.Window(count, selected, maxVisible);

        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedEnd, end);
        Assert.InRange(selected, start, end - 1);
    }

    [Fact]
    public void Footer_UsesTheCounterEvenWithoutAHint()
    {
        Assert.Contains("3/4", SlashDropdown.Footer(40, null, 3, 4), StringComparison.Ordinal);
    }
}
