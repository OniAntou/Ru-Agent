using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class InteractiveLineReaderTests
{
    [Fact]
    public void AutoComplete_ReturnsExactMatch_WhenPrefixMatchesSingleCandidate()
    {
        var candidates = new[] { "/help", "/model", "/backend", "/status" };

        var result1 = InteractiveLineReader.AutoComplete("/he", candidates);
        Assert.Equal("/help", result1);

        var result2 = InteractiveLineReader.AutoComplete("/mo", candidates);
        Assert.Equal("/model", result2);

        var result3 = InteractiveLineReader.AutoComplete("/B", candidates);
        Assert.Equal("/backend", result3);
    }

    [Fact]
    public void AutoComplete_ReturnsNull_WhenAmbiguousOrNoMatch()
    {
        var candidates = new[] { "/format", "/filter" };

        // Ambiguous prefix
        var ambiguous = InteractiveLineReader.AutoComplete("/f", candidates);
        Assert.Null(ambiguous);

        // No match
        var noMatch = InteractiveLineReader.AutoComplete("/unknown", candidates);
        Assert.Null(noMatch);

        // Not a slash command
        var normalText = InteractiveLineReader.AutoComplete("hello", candidates);
        Assert.Null(normalText);
    }

    [Fact]
    public void History_AppendsAndDedupesConsecutiveItems()
    {
        var reader = new InteractiveLineReader();
        reader.AddToHistory("first command");
        reader.AddToHistory("first command"); // consecutive duplicate
        reader.AddToHistory("second command");
        reader.AddToHistory("   "); // whitespace ignored
        reader.AddToHistory("first command"); // non-consecutive allowed

        Assert.Equal(3, reader.History.Count);
        Assert.Equal("first command", reader.History[0]);
        Assert.Equal("second command", reader.History[1]);
        Assert.Equal("first command", reader.History[2]);
    }

    [Theory]
    [InlineData(null, 4096, "")]
    [InlineData("", 4096, "")]
    [InlineData("sk-test-key", 4096, "sk-test-key")]
    [InlineData("  sk-test-key  \r\n", 4096, "sk-test-key")]
    [InlineData("sk-test-key\r\nsecond-line", 4096, "sk-test-key")]
    [InlineData("sk-test-key", 0, "")]
    [InlineData("sk-test-key", 6, "sk-tes")]
    public void SanitizePastedText_KeepsFirstTrimmedLineCapped(string? text, int maxChars, string expected)
    {
        Assert.Equal(expected, InteractiveLineReader.SanitizePastedText(text, maxChars));
    }

    [Fact]
    public async Task ReadLineWithContinuationAsync_CombinesLinesWithBackslash()
    {
        var reader = new InteractiveLineReader();
        var input = "line 1 \\\nline 2 \\\nline 3\n";
        using var inReader = new StringReader(input);
        using var outWriter = new StringWriter();

        var result = await reader.ReadLineWithContinuationAsync(
            inReader,
            outWriter,
            "prompt > ",
            "... > ");

        Assert.NotNull(result);
        Assert.Contains("line 1", result);
        Assert.Contains("line 2", result);
        Assert.Contains("line 3", result);
        Assert.Contains(result, reader.History);
    }

    [Fact]
    public async Task ReadSingleLineAsync_FallsBackGracefully_OnStringReader()
    {
        var reader = new InteractiveLineReader();
        using var inReader = new StringReader("single command line\n");
        using var outWriter = new StringWriter();

        var result = await reader.ReadSingleLineAsync(inReader, outWriter, "prompt > ");

        Assert.Equal("single command line", result);
        Assert.Equal("prompt > ", outWriter.ToString());
    }

    [Fact]
    public void FindMatches_ReturnsAllSlashCommands_WhenInputIsSingleSlash()
    {
        var matches = InteractiveLineReader.FindMatches("/");
        Assert.NotEmpty(matches);
        Assert.Contains(matches, m => m.Command == "/model");
        Assert.Contains(matches, m => m.Command == "/help");
        Assert.Contains(matches, m => m.Command == "/exit");
    }

    [Fact]
    public void FindMatches_FiltersPrefix_CaseInsensitively()
    {
        var matches = InteractiveLineReader.FindMatches("/m");
        Assert.Contains(matches, m => m.Command == "/model");
        Assert.DoesNotContain(matches, m => m.Command == "/help");

        var matchesUpper = InteractiveLineReader.FindMatches("/MO");
        Assert.Contains(matchesUpper, m => m.Command == "/model");

        var emptyMatches = InteractiveLineReader.FindMatches("/nonexistent");
        Assert.Empty(emptyMatches);
    }

    [Fact]
    public void BuildTop_And_BuildBottom_Support_Slash_Mode_Styling()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            var normalTop = InteractiveLineReader.BuildTop(40, isSlash: false);
            var slashTop = InteractiveLineReader.BuildTop(40, isSlash: true);
            Assert.Contains(TuiPalette.Code(TuiToken.Brand), slashTop, StringComparison.Ordinal);

            var normalBottom = InteractiveLineReader.BuildBottom(40, "legend", isSlash: false);
            var slashBottom = InteractiveLineReader.BuildBottom(40, "legend", isSlash: true);
            Assert.Contains(TuiPalette.Code(TuiToken.Brand), slashBottom, StringComparison.Ordinal);
            _ = normalTop;
            _ = normalBottom;
        }
    }

    [Fact]
    public void BuildDropdownTop_And_Bottom_Render_Correct_Frame()
    {
        var top = TuiText.Strip(InteractiveLineReader.BuildDropdownTop(50));
        Assert.StartsWith("╭", top);
        Assert.EndsWith("╮", top);
        Assert.Contains("commands", top);

        var bottom = TuiText.Strip(InteractiveLineReader.BuildDropdownBottom(50, 0, 5));
        Assert.StartsWith("╰", bottom);
        Assert.EndsWith("╯", bottom);
        Assert.Contains("1/5", bottom);
        Assert.Contains("↑↓ select", bottom);
    }

    [Fact]
    public void BuildDropdownRow_Highlights_Selected_Item()
    {
        var item = new InteractiveLineReader.SlashCommandInfo("/model", "Switch model", "/model [name]");
        var selectedRow = InteractiveLineReader.BuildDropdownRow(60, item, isSelected: true);
        var unselectedRow = InteractiveLineReader.BuildDropdownRow(60, item, isSelected: false);

        Assert.Contains("❯ /model", selectedRow);
        Assert.Contains("  /model", unselectedRow);
        Assert.Contains("Switch model", selectedRow);
    }

    [Fact]
    public void BuildBottom_With_Empty_Legend_Renders_Clean_Border_Without_Tooltips()
    {
        var cleanBottom = TuiText.Strip(InteractiveLineReader.BuildBottom(40, string.Empty));
        Assert.Equal("└" + new string('─', 38) + "┘", cleanBottom);
    }

    [Fact]
    public void BuildTop_And_BuildBottom_Scale_To_Fullscreen_Width()
    {
        int fullscreenWidth = 200;
        var top = TuiText.Strip(InteractiveLineReader.BuildTop(fullscreenWidth));
        var bottom = TuiText.Strip(InteractiveLineReader.BuildBottom(fullscreenWidth, "model · fast"));

        Assert.Equal(fullscreenWidth, top.Length);
        Assert.Equal("┌" + new string('─', fullscreenWidth - 2) + "┐", top);
        Assert.StartsWith("└", bottom);
        Assert.EndsWith("┘", bottom);
        Assert.Contains("model · fast", bottom);
    }

    [Theory]
    [InlineData(120, 100, 10)]
    [InlineData(80, 80, 0)]
    [InlineData(50, 80, 0)]
    [InlineData(100, 100, 0)]
    public void ComputeBoxLeft_CentersOnlyWithRoomToSpare(int viewport, int box, int expected) =>
        Assert.Equal(expected, InteractiveLineReader.ComputeBoxLeft(viewport, box));

    [Fact]
    public void ComposerMaxWidth_KeepsTheBoxNarrow()
    {
        Assert.True(InteractiveLineReader.ComposerMaxWidth <= 100);
        Assert.True(InteractiveLineReader.ComposerMaxWidth >= 40);
    }

    [Fact]
    public void BuildMid_WithMargin_IndentsAndMovesTheCursor()
    {
        var plain = InteractiveLineReader.BuildMidRewrite(40, "❯ ", "hi", 2, "", false);
        var indented = InteractiveLineReader.BuildMidRewrite(40, "❯ ", "hi", 2, "", false, 10);

        Assert.StartsWith("\r" + new string(' ', 10), indented, StringComparison.Ordinal);
        Assert.Contains("\r\x1b[", indented, StringComparison.Ordinal);

        static int CursorColumn(string rewrite)
        {
            var marker = "\r\x1b[";
            var start = rewrite.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length;
            var end = rewrite.IndexOf('C', start);
            return int.Parse(rewrite[start..end], System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.Equal(CursorColumn(plain) + 10, CursorColumn(indented));
    }

    [Fact]
    public void BuildBoxOpen_WithMargin_IndentsEveryRow()
    {
        var box = InteractiveLineReader.BuildBoxOpen(40, "❯ ", "legend", false, null, null, 6);

        foreach (var row in box.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var stripped = TuiText.Strip(row);
            if (stripped.Length == 0 || stripped.StartsWith('\x1b'))
            {
                continue;
            }

            Assert.StartsWith(new string(' ', 6), stripped, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WithSurfaceBg_FillsWithoutChangingWidth()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            var row = InteractiveLineReader.BuildMid(40, "❯ ", "hi", "", false);
            var filled = InteractiveLineReader.WithSurfaceBg(row);

            Assert.StartsWith(TuiPalette.SurfaceBgCode(), filled, StringComparison.Ordinal);
            Assert.EndsWith(TuiPalette.Reset, filled, StringComparison.Ordinal);
            Assert.Equal(TuiText.VisibleWidth(row), TuiText.VisibleWidth(filled));
        }
    }

    [Fact]
    public void WithSurfaceBg_IsNoopWithoutColour()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            var row = InteractiveLineReader.BuildMid(40, "❯ ", "hi", "", false);

            Assert.Equal(row, InteractiveLineReader.WithSurfaceBg(row));
        }
    }

    [Fact]
    public void BuildMid_WithBgAndMargin_OrdersMarginThenFill()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            var raw = InteractiveLineReader.BuildMid(40, "❯ ", "", "", false, 6, true);
            var row = TuiText.Strip(raw);

            Assert.StartsWith(new string(' ', 6), row, StringComparison.Ordinal);
            Assert.Contains(TuiPalette.SurfaceBgCode(), raw, StringComparison.Ordinal);
        }
    }
}
