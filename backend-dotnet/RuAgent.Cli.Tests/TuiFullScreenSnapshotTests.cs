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
    public void Header_PinnedSnapshot_ShowsTitleAndTally(int width)
    {
        var (writer, surface) = NewSurface();

        TuiHeader.RenderPinned(
            surface, width, topRow: 1,
            title: "Fix broken tests", tokens: 39413, height: 1);

        var sim = new TuiScreenSimulator(rows: Rows, cols: width);
        sim.Write(writer.ToString());

        Assert.Contains("# Fix broken tests", sim.VisibleRow(0), StringComparison.Ordinal);
        Assert.Contains("39,413", sim.VisibleRow(0), StringComparison.Ordinal);
        Assert.Empty(sim.VisibleRow(1));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(160)]
    public void ToolRow_SettledSnapshot_ShowsInlineMutedRow(int width)
    {
        var (writer, surface) = NewSurface();

        surface.WriteLine(TuiToolRow.Compose(
            ToolLineStatus.Ok, "Search", "\"homepage\"", "(18 matches)", width));

        // One spare row: the trailing newline of the last write would otherwise scroll
        // the first row off a shorter screen, exactly like a real terminal at the bottom.
        var sim = new TuiScreenSimulator(rows: 2, cols: width);
        sim.Write(writer.ToString());

        Assert.StartsWith("✱ Search \"homepage\" (18 matches)", sim.VisibleRow(0), StringComparison.Ordinal);
        Assert.True(TuiText.VisibleWidth(sim.VisibleRow(0)) <= width);
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
        Assert.Contains("idle session", sim.VisibleRow(0), StringComparison.Ordinal);
        Assert.DoesNotContain("assistant line", sim.VisibleRow(0), StringComparison.Ordinal);

        // The transcript is present and never leaks into the fixed five-row footer.
        Assert.Contains("assistant line 59", sim.ScreenText(), StringComparison.Ordinal);
        Assert.Contains("┌", sim.VisibleRow(Rows - 5), StringComparison.Ordinal);
        Assert.Contains("next request", sim.VisibleRow(Rows - 4), StringComparison.Ordinal);
        Assert.Contains("Standard", sim.VisibleRow(Rows - 3), StringComparison.Ordinal);
        Assert.Contains("/ commands", sim.VisibleRow(Rows - 1), StringComparison.Ordinal);
        for (var row = Rows - 5; row < Rows; row++)
        {
            Assert.DoesNotContain("assistant line", sim.VisibleRow(row), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public async Task ErrorLine_InsideTheAltFrame_LandsInTheTranscriptAndKeepsTheHeaderPinned(int width)
    {
        var (writer, surface) = NewSurface();
        var region = new TuiStickyRegion(surface);
        var layout = TuiScreenLayout.Compute(width, Rows);
        TuiHeader.RenderPinned(
            surface, width, layout.HeaderTop + 1,
            "Error recovery session", 1200, layout.HeaderHeight);
        Assert.True(region.Enter(Rows, layout.FooterHeight, width, topRow: layout.TranscriptTop + 1));
        for (var i = 0; i < 40; i++)
        {
            surface.WriteLine($"assistant line {i:D2}");
        }

        // The footer paint parks the hardware cursor on the composer row; a raw error
        // write here smudges the frame and its newline scrolls the whole screen.
        PaintIdleFooter(surface, region, width, Rows);

        await AgentCommands.WriteAltSafeAsync(
            region,
            altBufferActive: true,
            writer,
            "assistant_text_truncated: 12 of 34 characters reached this terminal.");

        // The next footer repaint must not erase the line: it belongs to the transcript.
        PaintIdleFooter(surface, region, width, Rows);

        var sim = new TuiScreenSimulator(rows: Rows, cols: width);
        sim.Write(writer.ToString());

        // The header stays on its original row.
        Assert.Contains("Error recovery session", sim.VisibleRow(0), StringComparison.Ordinal);
        Assert.DoesNotContain("assistant_text_truncated", sim.VisibleRow(0), StringComparison.Ordinal);

        // The error line is visible inside the transcript region, not in the footer.
        var footerFirst = Rows - layout.FooterHeight;
        var inTranscript = false;
        for (var row = layout.TranscriptTop; row < footerFirst; row++)
        {
            if (sim.VisibleRow(row).Contains("assistant_text_truncated", StringComparison.Ordinal))
            {
                inTranscript = true;
            }
        }

        Assert.True(inTranscript, "the error line must be visible inside the transcript region");
        for (var row = footerFirst; row < Rows; row++)
        {
            Assert.DoesNotContain("assistant_text_truncated", sim.VisibleRow(row), StringComparison.Ordinal);
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
            "Resize session", 88, layout.HeaderHeight);
        Assert.True(region.Enter(40, layout.FooterHeight, 120, topRow: layout.TranscriptTop + 1));

        var budget = 40 - layout.FooterHeight - 2;
        foreach (var line in surface.SnapshotTail(budget, 120))
        {
            surface.WriteLine(line);
        }

        PaintIdleFooter(surface, region, 120, 40);

        var sim = new TuiScreenSimulator(rows: 40, cols: 120);
        sim.Write(writer.ToString());

        Assert.Contains("Resize session", sim.VisibleRow(0), StringComparison.Ordinal);
        Assert.Contains("assistant tail marker", sim.ScreenText(), StringComparison.Ordinal);
        Assert.Contains("┌", sim.VisibleRow(35), StringComparison.Ordinal);
        for (var row = 35; row < 40; row++)
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
            "idle session", 1234, layout.HeaderHeight);
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
            "Frame session", 42, layout.HeaderHeight);
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
        List<string> footer =
        [
            .. ComposerBox.ComposeStack(
                width, ComposerState.Idle, "next request", string.Empty, "Standard", "gpt-5", "OpenAI"),
            ComposerBox.ComposeHints(width, null, "D:/LLM"),
        ];
        region.Paint(footer, width, caretRowInFooter: 2, caretColumn: 3);
    }
}
