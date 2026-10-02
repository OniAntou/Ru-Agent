using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// T5b snapshots for the full-screen AltBuffer frame: header, tool card, highlighted code,
/// table, floating approval panel, the persisted idle frame (header + fixed footer while the
/// transcript scrolls) and the resize rebuild. Every assertion goes through
/// <see cref="TuiScreenSimulator"/>, so it proves what the operator sees rather than which
/// bytes were written. Widths 80/120/160 cover the minimum, the common and the wide terminal.
/// </summary>
public sealed class TuiFullScreenSnapshotTests
{
    private const int Rows = 30;

    private static (StringWriter Writer, TuiSurface Surface) NewSurface()
    {
        TuiPalette.Level = TuiColorLevel.None;
        var writer = new StringWriter();
        return (writer, new TuiSurface(writer) { SynchronizedOutput = false });
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(160)]
    public void Header_PinnedSnapshot_ShowsIdentityAndContext(int width)
    {
        var (writer, surface) = NewSurface();

        TuiHeader.RenderPinned(
            surface, width, topRow: 1,
            workspace: "D:\\LLM", backend: "OpenAI", model: "gpt-5",
            preset: "standard", sessionId: "sess_1234567890", height: 2);

        var sim = new TuiScreenSimulator(rows: Rows, cols: width);
        sim.Write(writer.ToString());

        Assert.Contains("Ru", sim.VisibleRow(0), StringComparison.Ordinal);
        Assert.Contains("sess_123", sim.VisibleRow(0), StringComparison.Ordinal);
        Assert.Contains("D:/LLM", sim.VisibleRow(1), StringComparison.Ordinal);
        Assert.Contains("OpenAI/gpt-5", sim.VisibleRow(1), StringComparison.Ordinal);
        Assert.Empty(sim.VisibleRow(2));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(160)]
    public void ToolCard_FullAndCollapsedSnapshot_FramesDiffHunks(int width)
    {
        var (writer, surface) = NewSurface();
        const string Title = "✓ edited src/app.cs · +3 -1 · 12ms";

        foreach (var row in TuiToolCard.Render(
            Title,
            ["@@ -1,3 +1,4 @@", "- old line", "+ new line", " context"],
            width,
            TuiColorLevel.None))
        {
            surface.WriteLine(row);
        }

        surface.WriteLine(TuiToolCard.RenderSummary(Title, width, expandable: true, TuiColorLevel.None));

        // One spare row: the trailing newline of the last write would otherwise scroll
        // the first row off a shorter screen, exactly like a real terminal at the bottom.
        var sim = new TuiScreenSimulator(rows: 9, cols: width);
        sim.Write(writer.ToString());

        Assert.StartsWith("╭", sim.VisibleRow(0), StringComparison.Ordinal);
        Assert.Contains("edited src/app.cs", sim.VisibleRow(1), StringComparison.Ordinal);
        Assert.Contains("- old line", sim.VisibleRow(3), StringComparison.Ordinal);
        Assert.Contains("+ new line", sim.VisibleRow(4), StringComparison.Ordinal);
        Assert.StartsWith("╰", sim.VisibleRow(6), StringComparison.Ordinal);
        Assert.Contains("ctrl+o", sim.VisibleRow(7), StringComparison.Ordinal);
        Assert.Equal(width, TuiText.VisibleWidth(sim.VisibleRow(0)));
        Assert.Equal(width, TuiText.VisibleWidth(sim.VisibleRow(7)));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(160)]
    public void CodeBlock_HighlightSnapshot_NumbersAndTintsEveryRow(int width)
    {
        var (writer, surface) = NewSurface();
        var markdown = new TuiMarkdown { HighlightCode = true };

        var rows = new List<string>();
        foreach (var raw in new[] { "```cs", "var x = 1; // note", "Console.WriteLine(\"hi\");", "```" })
        {
            rows.AddRange(markdown.RenderLine(raw, width));
        }

        Assert.Equal(2, rows.Count);
        Assert.StartsWith("  1 │", rows[0], StringComparison.Ordinal);
        Assert.StartsWith("  2 │", rows[1], StringComparison.Ordinal);
        Assert.Contains("var x = 1;", rows[0], StringComparison.Ordinal);
        Assert.Contains("\"hi\"", rows[1], StringComparison.Ordinal);

        foreach (var row in rows)
        {
            surface.WriteLine(row);
        }

        var sim = new TuiScreenSimulator(rows: 3, cols: width);
        sim.Write(writer.ToString());
        Assert.Contains("  1 │", sim.VisibleRow(0), StringComparison.Ordinal);
        Assert.Contains("  2 │", sim.VisibleRow(1), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(160)]
    public void TableAndNestedList_Snapshot_FlowsThroughMarkdown(int width)
    {
        var (writer, surface) = NewSurface();
        var markdown = new TuiMarkdown();

        var table = markdown.RenderLine("| name | count |", width);
        var nested = markdown.RenderLine("  - nested item", width);
        Assert.Contains("name │ count", table[0], StringComparison.Ordinal);
        Assert.StartsWith("    •", nested[0], StringComparison.Ordinal);
        Assert.Contains("nested item", nested[0], StringComparison.Ordinal);

        surface.WriteLine(table[0]);
        surface.WriteLine(nested[0]);

        var sim = new TuiScreenSimulator(rows: 3, cols: width);
        sim.Write(writer.ToString());
        Assert.Contains("name │ count", sim.VisibleRow(0), StringComparison.Ordinal);
        Assert.Contains("nested item", sim.VisibleRow(1), StringComparison.Ordinal);
        Assert.True(TuiText.VisibleWidth(sim.VisibleRow(0)) <= width);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(160)]
    public void ApprovalPanel_FloatingSnapshot_IsCenteredAndKeepsQuickPick(int width)
    {
        var (writer, surface) = NewSurface();
        var prompt = ApprovalPrompt.For("terminal_delete_requires_approval");

        var rows = prompt.RenderFloating(width);
        var panelWidth = TuiFloatingPanel.ComputeWidth(width);
        var left = (width - panelWidth) / 2;

        Assert.StartsWith(new string(' ', left), rows[0], StringComparison.Ordinal);
        Assert.All(rows, row => Assert.Equal(left + panelWidth, TuiText.VisibleWidth(row)));
        Assert.Contains("approval", rows[0], StringComparison.Ordinal);
        Assert.Contains("1 · once", rows[1], StringComparison.Ordinal);
        Assert.Contains("2 · deny", rows[2], StringComparison.Ordinal);
        Assert.Contains("3 · always", rows[3], StringComparison.Ordinal);
        Assert.Contains("esc", rows[^1], StringComparison.Ordinal);

        foreach (var row in rows)
        {
            surface.WriteLine(row);
        }

        var sim = new TuiScreenSimulator(rows: rows.Count + 1, cols: width);
        sim.Write(writer.ToString());
        Assert.Contains("1 · once", sim.ScreenText(), StringComparison.Ordinal);
        Assert.StartsWith(new string(' ', left), sim.VisibleRow(0), StringComparison.Ordinal);

        Assert.True(prompt.TrySelectDigit('3'));
        Assert.Equal(ApprovalPrompt.AllowAlways, prompt.Current.Decision);
        Assert.Equal(
            ChoiceOutcome.Cancelled,
            prompt.HandleKey(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false)));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(160)]
    public void IdleFrame_HeaderAndFixedFooterPersistWhileTranscriptScrolls(int width)
    {
        var (writer, _, _) = BuildIdleFrame(width, rows: Rows, transcriptLines: 60);
        var sim = new TuiScreenSimulator(rows: Rows, cols: width);
        sim.Write(writer.ToString());

        // Header stays pinned; nothing scrolled over it.
        Assert.Contains("Ru", sim.VisibleRow(0), StringComparison.Ordinal);
        Assert.Contains("OpenAI/gpt-5", sim.VisibleRow(1), StringComparison.Ordinal);
        Assert.DoesNotContain("assistant line", sim.VisibleRow(0), StringComparison.Ordinal);
        Assert.DoesNotContain("assistant line", sim.VisibleRow(1), StringComparison.Ordinal);

        // The transcript is present and never leaks into the fixed three-row footer.
        Assert.Contains("assistant line 59", sim.ScreenText(), StringComparison.Ordinal);
        Assert.Contains("idle", sim.VisibleRow(Rows - 3), StringComparison.Ordinal);
        Assert.Contains("next request", sim.VisibleRow(Rows - 1), StringComparison.Ordinal);
        for (var row = Rows - 3; row < Rows; row++)
        {
            Assert.DoesNotContain("assistant line", sim.VisibleRow(row), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Resize_RebuildKeepsTailAndFrame()
    {
        var (writer, surface) = NewSurface();
        var region = new TuiStickyRegion(surface);
        BuildFrame(surface, region, width: 80, rows: 30, tail: "assistant tail marker");

        // The operator grows the window: reset, clear, rebuild from the bounded history.
        region.Exit();
        surface.Repaint(w => w.Write("\u001b[r\u001b[3J\u001b[2J\u001b[H"));
        var layout = TuiScreenLayout.Compute(120, 40);
        TuiHeader.RenderPinned(
            surface, 120, layout.HeaderTop + 1,
            "D:\\LLM", "OpenAI", "gpt-5", "standard", "sess_resize", layout.HeaderHeight);
        Assert.True(region.Enter(40, layout.FooterHeight, 120, topRow: layout.TranscriptTop + 1));

        var budget = 40 - layout.FooterHeight - 2;
        foreach (var line in surface.SnapshotTail(budget, 120))
        {
            surface.WriteLine(line);
        }

        PaintIdleFooter(surface, region, 120, 40);

        var sim = new TuiScreenSimulator(rows: 40, cols: 120);
        sim.Write(writer.ToString());

        Assert.Contains("Ru", sim.VisibleRow(0), StringComparison.Ordinal);
        Assert.Contains("assistant tail marker", sim.ScreenText(), StringComparison.Ordinal);
        Assert.Contains("idle", sim.VisibleRow(37), StringComparison.Ordinal);
        for (var row = 37; row < 40; row++)
        {
            Assert.DoesNotContain("assistant tail marker", sim.VisibleRow(row), StringComparison.Ordinal);
        }

        for (var row = 0; row < 2; row++)
        {
            Assert.DoesNotContain("assistant tail marker", sim.VisibleRow(row), StringComparison.Ordinal);
        }
    }

    private static (StringWriter Writer, TuiSurface Surface, TuiStickyRegion Region) BuildIdleFrame(
        int width, int rows, int transcriptLines)
    {
        var (writer, surface) = NewSurface();
        var region = new TuiStickyRegion(surface);
        var layout = TuiScreenLayout.Compute(width, rows);
        TuiHeader.RenderPinned(
            surface, width, layout.HeaderTop + 1,
            "D:\\LLM", "OpenAI", "gpt-5", "standard", "sess_12345678", layout.HeaderHeight);
        Assert.True(region.Enter(rows, layout.FooterHeight, width, topRow: layout.TranscriptTop + 1));
        for (var i = 0; i < transcriptLines; i++)
        {
            surface.WriteLine($"assistant line {i:D2}");
        }

        PaintIdleFooter(surface, region, width, rows);
        return (writer, surface, region);
    }

    private static void BuildFrame(
        TuiSurface surface, TuiStickyRegion region, int width, int rows, string tail)
    {
        var layout = TuiScreenLayout.Compute(width, rows);
        TuiHeader.RenderPinned(
            surface, width, layout.HeaderTop + 1,
            "D:\\LLM", "OpenAI", "gpt-5", "standard", "sess_resize", layout.HeaderHeight);
        Assert.True(region.Enter(rows, layout.FooterHeight, width, topRow: layout.TranscriptTop + 1));
        surface.WriteLine("assistant answer head");
        surface.WriteLine(tail);
        PaintIdleFooter(surface, region, width, rows);
    }

    private static void PaintIdleFooter(TuiSurface surface, TuiStickyRegion region, int width, int rows)
    {
        _ = rows;
        var status = new TuiStatusLine(surface);
        status.SetState(TuiRunState.Idle);
        status.SetElapsed(12.3);
        var footer = new List<string>
        {
            status.Compose(width),
            TuiToolCard.RenderLiveRow(null, width, TuiColorLevel.None),
            ComposerBox.ComposeRow(width, ComposerState.Idle, "❯ ", "next request", string.Empty, "gpt-5"),
        };
        region.Paint(footer, width, caretRowInFooter: 3, caretColumn: 3);
    }
}
