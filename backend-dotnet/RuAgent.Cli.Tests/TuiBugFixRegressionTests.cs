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

    [Fact]
    public void BuildBoxOpen_IncludesWorkspaceTopLabelAndBottomHints()
    {
        var box = InteractiveLineReader.BuildBoxOpen(
            80,
            "❯ ",
            "Kilo Gateway · model",
            isSlash: false,
            topLabel: "C:/Users/USER · standard",
            hints: "/ commands · ↑↓ history");

        Assert.Contains("C:/Users/USER · standard", box);
        Assert.Contains("Kilo Gateway · model", box);
        Assert.Contains("/ commands · ↑↓ history", box);
        Assert.EndsWith("\x1b[2A", box);
        Assert.DoesNotContain("\x1b[2A\n", box);
        Assert.DoesNotContain("\x1b[2A\r", box);

        var boxNoHints = InteractiveLineReader.BuildBoxOpen(
            80,
            "❯ ",
            "model-test",
            isSlash: false,
            topLabel: null,
            hints: null);
        Assert.Contains("\x1b[1A", boxNoHints);
        Assert.EndsWith("\x1b[1A", boxNoHints);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void BuildDropdownRenderSequence_NetVerticalDisplacementIsZero(int count)
    {
        var lines = Enumerable.Range(0, count).Select(i => $"item {i}").ToList();
        var seq = InteractiveLineReader.BuildDropdownRenderSequence(lines);

        // Starts by stepping down 2 rows to the hints row
        Assert.StartsWith("\x1b[2B", seq);

        // Ends by stepping back up to Mid (count + 1 rows up)
        Assert.EndsWith($"\r\x1b[{count + 1}A", seq);

        // Does NOT end with newline before stepping up
        Assert.DoesNotContain($"\n\r\x1b[{count + 1}A", seq);

        // Count newlines: should have exactly count - 1 newlines (no trailing newline on last line)
        int newlineCount = seq.Count(c => c == '\n');
        Assert.Equal(count - 1, newlineCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void BuildDropdownClearSequence_RestoresHintsAndReturnsToMid(int count)
    {
        const string hints = "  / commands · ↑↓ history";
        var seq = InteractiveLineReader.BuildDropdownClearSequence(count, 80, hints);

        // Steps down 2 rows to hints row and clears it
        Assert.StartsWith("\x1b[2B\r\x1b[2K", seq);

        // Restores hints on the hints row (immediately following the step-down)
        Assert.Contains(hints, seq);

        // Must step back up by count + 1 rows to land exactly at Mid row (never fixed \x1b[2A)
        Assert.EndsWith($"\r\x1b[{count + 1}A", seq);

        // Net vertical displacement: 2 down + (count - 1) down - (count + 1) up = 0
        int stepDownCount = Regex.Matches(seq, @"\x1b\[1B").Count;
        Assert.Equal(count - 1, stepDownCount);
    }

    [Fact]
    public void DropdownLifecycle_TypingSlashAndFilteringToCommand_MaintainsCorrectGeometry()
    {
        const string hints = "/ commands · ↑↓ history · tab complete";

        // Step 1: User types '/' -> 7 items dropdown rendered
        var fullMatches = Enumerable.Range(0, 7).Select(i => $"cmd {i}").ToList();
        var render7 = InteractiveLineReader.BuildDropdownRenderSequence(fullMatches);
        Assert.StartsWith("\x1b[2B", render7);
        Assert.EndsWith("\r\x1b[8A", render7);

        // Step 2: User types 'm' -> narrows to 3 items -> ClearDropdown(7) then Render(3)
        var clear7 = InteractiveLineReader.BuildDropdownClearSequence(7, 80, hints);
        Assert.StartsWith("\x1b[2B\r\x1b[2K", clear7);
        Assert.EndsWith("\r\x1b[8A", clear7); // Crucial: must step up 8, NOT 2!
        Assert.Contains(hints, clear7);

        var narrowMatches = Enumerable.Range(0, 3).Select(i => $"cmd {i}").ToList();
        var render3 = InteractiveLineReader.BuildDropdownRenderSequence(narrowMatches);
        Assert.StartsWith("\x1b[2B", render3);
        Assert.EndsWith("\r\x1b[4A", render3);

        // Step 3: User confirms/escapes -> ClearDropdown(3)
        var clear3 = InteractiveLineReader.BuildDropdownClearSequence(3, 80, hints);
        Assert.StartsWith("\x1b[2B\r\x1b[2K", clear3);
        Assert.EndsWith("\r\x1b[4A", clear3); // Crucial: must step up 4, NOT 2!
        Assert.Contains(hints, clear3);
    }

    [Theory]
    [InlineData(27, 7, 30, true)]   // Box at bottom (rows 26-29), 7 lines dropdown -> scrolls
    [InlineData(27, 1, 30, false)]  // 1 line fits at row 29 without scroll
    [InlineData(27, 2, 30, true)]   // 2 lines exceed available 1 -> scrolls
    [InlineData(10, 7, 30, false)]  // Box high up -> plenty of room, does not scroll
    [InlineData(0, 0, 30, false)]   // Empty lines -> does not scroll
    [InlineData(25, 7, 0, false)]   // Invalid rows -> does not scroll
    public void DidDropdownScroll_CorrectlyIdentifiesWhenTerminalWillScroll(int cursorTop, int linesCount, int windowRows, bool expected)
    {
        var result = InteractiveLineReader.DidDropdownScroll(cursorTop, linesCount, windowRows);
        Assert.Equal(expected, result);
    }
}

