using System.Globalization;
using System.Text.RegularExpressions;
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiBugFixRegressionTests
{
    [Fact]
    public void Wrap_NarrowWidthWithWideChar_DoesNotHangInInfiniteLoop()
    {
        // Width 1 is smaller than Emoji width 2
        var lines1 = TuiText.Wrap("🔥", 1);
        Assert.Single(lines1);
        Assert.Equal("🔥", lines1[0]);

        // Width 1 with multiple wide CJK characters
        var lines2 = TuiText.Wrap("你好世界", 1);
        Assert.Equal(4, lines2.Count);
        Assert.Equal(["你", "好", "世", "界"], lines2);
    }

    [Fact]
    public void Tail_WithSurrogatePair_DoesNotLeaveOrphanSurrogate()
    {
        // "Hello 👋" has length 8 (👋 is 2 UTF-16 code units, visible width 2)
        var text = "Hello 👋";
        // When width is 2, budget is 1 (for ellipsis); 👋 needs 2 cells, so only ellipsis fits
        var tail2 = TuiText.Tail(text, 2);
        Assert.Equal("…", tail2);

        // When width is 3, budget is 2; 👋 fits and is prefixed by ellipsis
        var tail3 = TuiText.Tail(text, 3);
        Assert.Equal("…👋", tail3);
        Assert.Equal(3, TuiText.VisibleWidth(tail3));
    }

    [Fact]
    public void Tail_WithAnsiCodes_PreservesOrSkipsAnsiCleanly()
    {
        var colored = "\u001b[31mHello\u001b[0m";
        var tail = TuiText.Tail(colored, 3);
        // Stripped result should be 3 visible cells ("…" + "lo")
        Assert.Equal(3, TuiText.VisibleWidth(tail));
        Assert.Equal("…lo", TuiText.Strip(tail));
    }

    [Fact]
    public void ChoiceList_SkipsDisabledItemsOnNavigationAndSelection()
    {
        var items = new List<ChoiceItem>
        {
            new("Item 1", "desc 1", "first", Enabled: false),
            new("Item 2", "desc 2", "second", Enabled: true),
            new("Item 3", "desc 3", "third", Enabled: false),
            new("Item 4", "desc 4", "fourth", Enabled: true),
        };

        var list = new ChoiceList("Options", items);

        // Initial selection should land on the first enabled item (index 1)
        Assert.Equal(1, list.Selected);
        Assert.Equal("second", list.Current.Value);

        // Move next should skip index 2 (disabled) and land on index 3
        list.Move(1);
        Assert.Equal(3, list.Selected);
        Assert.Equal("fourth", list.Current.Value);

        // Move next wraps around, skipping index 0 (disabled), landing on index 1
        list.Move(1);
        Assert.Equal(1, list.Selected);
        Assert.Equal("second", list.Current.Value);

        // Selecting digit '1' corresponds to index 0 which is disabled
        Assert.False(list.TrySelectDigit('1'));

        // Selecting digit '2' corresponds to index 1 which is enabled
        Assert.True(list.TrySelectDigit('2'));
        Assert.Equal(1, list.Selected);
        Assert.Equal("second", list.Current.Value);
    }

    [Fact]
    public void TuiPanel_Compose_DoesNotPrematurelyTruncateDetail()
    {
        // width = 40. inner = 40 - 4 = 36. contentBudget = inner - 2 = 34.
        // LabelColumn = 18. label = "label" padded to 18.
        // detailWidth = contentBudget - labelWidth - 1 = 34 - 18 - 1 = 15.
        // detail with 15 characters exactly fits detailWidth.
        var width = 40;
        var detail = new string('x', 15);
        var sections = new List<PanelSection>
        {
            new(null, [new PanelRow("label", detail)])
        };

        var lines = TuiPanel.Compose(width, "test", sections);
        var rowLine = lines.FirstOrDefault(l => l.Contains("label", StringComparison.Ordinal));
        Assert.NotNull(rowLine);
        var stripped = TuiText.Strip(rowLine);

        Assert.Equal(width, TuiText.VisibleWidth(stripped));
        // detail 'xxxxxxxxxxxxxxx' must not be truncated with '…'
        Assert.Contains(detail, stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("…", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void TuiStatusLine_FormatTokens_SupportsMillions()
    {
        Assert.Equal("1.5M", TuiStatusLine.FormatTokens(1_500_000));
        Assert.Equal("10M", TuiStatusLine.FormatTokens(10_000_000));
        Assert.Equal("500K", TuiStatusLine.FormatTokens(500_000));
        Assert.Equal("999", TuiStatusLine.FormatTokens(999));
    }

    [Fact]
    public void TuiStatusLine_FormatElapsed_UsesInvariantCulture()
    {
        // FormatElapsed should always use dot as decimal separator
        var elapsed = TuiStatusLine.FormatElapsed(1.5);
        Assert.Equal("1.5s", elapsed);
    }


}

