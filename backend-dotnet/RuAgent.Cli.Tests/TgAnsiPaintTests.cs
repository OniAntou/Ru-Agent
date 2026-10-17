using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.Drawing;
using Xunit;
using Attr = Terminal.Gui.Drawing.Attribute;

namespace RuAgent.Cli.Tests;

/// <summary>
/// Pure coverage for the escape-to-span parser and the span-aware wrappers: the palette
/// escapes the renderer paints must come back as exactly the attributes they meant, with
/// no escape bytes left in the visible text.
/// </summary>
public sealed class TgAnsiPaintTests
{
    [Fact]
    public void Parse_NoEscapes_YieldsNullSpans()
    {
        var (clean, spans) = TgAnsiPaint.Parse("plain prose");
        Assert.Equal("plain prose", clean);
        Assert.Null(spans);
    }

    [Fact]
    public void Parse_TrueColorPaint_YieldsOneSpanWithTheTokenColour()
    {
        using var _ = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        var painted = TuiPalette.Paint("warn text", TuiToken.Warn);
        var (clean, spans) = TgAnsiPaint.Parse(painted);

        Assert.Equal("warn text", clean);
        Assert.DoesNotContain('\u001b', clean);
        var span = Assert.Single(spans!);
        Assert.Equal("warn text", span.Text);
        Assert.Equal(TgPalette.ToColor(TuiPalette.Rgb(TuiToken.Warn)), span.Attr.Foreground);
    }

    [Fact]
    public void Parse_Palette256Paint_MapsBackToTheTokenRgb()
    {
        using var _ = TuiPalette.PinLevel(TuiColorLevel.Palette256);
        var painted = TuiPalette.Paint("ok", TuiToken.Ok);
        var (clean, spans) = TgAnsiPaint.Parse(painted);

        Assert.Equal("ok", clean);
        var span = Assert.Single(spans!);
        Assert.StartsWith("\u001b[38;5;79m", painted);
        // Index 79 is the standard 6x6x6 cube cell 63: channel values 1, 4, 3.
        Assert.Equal(TgPalette.ToColor((95, 215, 175)), span.Attr.Foreground);
    }

    [Fact]
    public void Parse_Basic16Paint_MapsBackToTheTokenRgb()
    {
        using var _ = TuiPalette.PinLevel(TuiColorLevel.Basic16);
        var painted = TuiPalette.Paint("err", TuiToken.Err);
        var (clean, spans) = TgAnsiPaint.Parse(painted);

        Assert.Equal("err", clean);
        var span = Assert.Single(spans!);
        // Err maps to basic index 9: standard bright red (255,0,0), not the token RGB.
        Assert.Equal(TgPalette.ToColor((255, 0, 0)), span.Attr.Foreground);
    }

    [Fact]
    public void Parse_BoldPaint_CarriesTheBoldStyle()
    {
        using var _ = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        var painted = TuiPalette.BoldPaint("loud");
        var (clean, spans) = TgAnsiPaint.Parse(painted);

        Assert.Equal("loud", clean);
        var span = Assert.Single(spans!);
        Assert.Equal(TextStyle.Bold, span.Attr.Style);
    }

    [Fact]
    public void Parse_DimPaint_BlendsTheForegroundTowardTheBackground()
    {
        using var _ = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        var painted = TuiPalette.DimPaint("quiet");
        var (clean, spans) = TgAnsiPaint.Parse(painted);

        Assert.Equal("quiet", clean);
        var span = Assert.Single(spans!);
        // Ink (238,238,238) blended halfway with the black row background).
        Assert.Equal(TgPalette.ToColor((119, 119, 119)), span.Attr.Foreground);
    }

    [Fact]
    public void Parse_PaintedOverDefault_SplitsSpansAtEveryReset()
    {
        using var _ = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        var painted = TuiPalette.Ok("good")
            + " then plain "
            + TuiPalette.Err("bad");
        var (clean, spans) = TgAnsiPaint.Parse(painted);

        Assert.Equal("good then plain bad", clean);
        Assert.DoesNotContain('\u001b', clean);
        Assert.Equal(3, spans!.Count);
        Assert.Equal(TgPalette.ToColor(TuiPalette.Rgb(TuiToken.Ok)), spans[0].Attr.Foreground);
        Assert.Equal("good", spans[0].Text);
        Assert.Equal(" then plain ", spans[1].Text);
        Assert.Equal(TgPalette.ToColor(TuiPalette.Rgb(TuiToken.Err)), spans[2].Attr.Foreground);
        Assert.Equal("bad", spans[2].Text);
    }

    [Fact]
    public void Parse_NestedBrightBold_KeepsColorAndStyleTogether()
    {
        using var _ = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        // The renderer wraps bold inside a colour paint, so the colour opens first.
        var painted = TuiPalette.Paint(TuiPalette.BoldPaint("shout"), TuiToken.Bright);
        var (clean, spans) = TgAnsiPaint.Parse(painted);

        Assert.Equal("shout", clean);
        var span = Assert.Single(spans!);
        Assert.Equal(TextStyle.Bold, span.Attr.Style);
        Assert.Equal(TgPalette.ToColor(TuiPalette.Rgb(TuiToken.Bright)), span.Attr.Foreground);
    }

    [Fact]
    public void Parse_NonSgrEscapeIsDroppedInvisibly()
    {
        // A cursor move may ride along in tool output; it vanishes and changes no paint.
        var (clean, spans) = TgAnsiPaint.Parse("a\u001b[1;2Hb\u001b[2Kc");
        Assert.Equal("abc", clean);
        Assert.Null(spans);
    }

    [Fact]
    public void Parse_TableRowsAndMultibyteSurvive()
    {
        using var _ = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        var painted = TuiPalette.Paint("kiểm tra", TuiToken.Ok);
        var (clean, spans) = TgAnsiPaint.Parse(painted);
        Assert.Equal("kiểm tra", clean);
        Assert.Equal("kiểm tra", Assert.Single(spans!).Text);
    }

    [Fact]
    public void WordWrapSpans_SplitsRowsWithoutLosingCharacters()
    {
        using var _ = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        var painted = TuiPalette.Ok("alpha ") + "beta gamma";
        var (clean, spans) = TgAnsiPaint.Parse(painted);
        Assert.NotNull(spans);

        var rows = TgAnsiPaint.WordWrapSpans(clean, spans!, 8);
        Assert.All(rows, row => Assert.True(TuiText.VisibleWidth(row.Row) <= 8));
        Assert.Equal(clean.Replace(" ", string.Empty).Length, rows.Sum(row => row.Row.Replace(" ", string.Empty).Length));

        // Every row is fully covered by its spans.
        foreach (var row in rows)
        {
            Assert.Equal(row.Row, string.Concat(row.Spans.Select(span => span.Text)));
        }

        Assert.Contains(rows, row => row.Row.Contains("gamma"));
    }

    [Fact]
    public void WordWrapSpans_CutsASpanAtTheRowBoundary()
    {
        using var _ = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        var painted = TuiPalette.Ok("one two three");
        var (clean, spans) = TgAnsiPaint.Parse(painted);

        var rows = TgAnsiPaint.WordWrapSpans(clean, spans!, 8);
        Assert.Equal(2, rows.Count);
        Assert.Equal("one two", rows[0].Row);
        Assert.Equal("three", rows[1].Row);
        Assert.All(rows, row => Assert.Equal(row.Row, string.Concat(row.Spans.Select(span => span.Text))));
        Assert.All(rows, row => Assert.All(row.Spans, span => Assert.Equal(TgPalette.ToColor(TuiPalette.Rgb(TuiToken.Ok)), span.Attr.Foreground)));
    }

    [Fact]
    public void HardWrapSpans_SplitsColumnsKeepingBackticksGutterRowsAligned()
    {
        using var _ = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        var painted = TuiPalette.DimPaint("  │ 1 ") + TuiPalette.Paint("x" + new string('y', 14), TuiToken.Ok);
        var (clean, spans) = TgAnsiPaint.Parse(painted);

        var rows = TgAnsiPaint.HardWrapSpans(clean, spans!, 10);
        Assert.Equal(clean, string.Concat(rows.Select(row => row.Row)));
        Assert.All(rows, row => Assert.Equal(row.Row, string.Concat(row.Spans.Select(span => span.Text))));
    }

    [Fact]
    public void WordWrapSpans_KeepsParagraphBreaksAsRows()
    {
        var text = "one\ntwo";
        var spans = new List<TgSpan> { new(text, TgPalette.Attr(TuiToken.Ink)) };
        var rows = TgAnsiPaint.WordWrapSpans(text, spans, 40);
        Assert.Equal(2, rows.Count);
        Assert.Equal("one", rows[0].Row);
        Assert.Equal("two", rows[1].Row);
    }

    [Fact]
    public void WordWrapSpans_TrailingNewlineEndsAnEmptyRow()
    {
        var text = "one\n";
        var spans = new List<TgSpan> { new(text, TgPalette.Attr(TuiToken.Ink)) };
        var rows = TgAnsiPaint.WordWrapSpans(text, spans, 40);
        Assert.Equal(2, rows.Count);
        Assert.Equal("one", rows[0].Row);
        Assert.Equal(string.Empty, rows[1].Row);
    }
}
