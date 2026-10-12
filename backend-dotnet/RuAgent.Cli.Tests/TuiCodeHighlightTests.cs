using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiCodeHighlightTests
{
    [Fact]
    public void RenderLine_NumbersEveryRow()
    {
        var rows = TuiCodeHighlight.RenderLine("var x = 1;", 1, 40, TuiColorLevel.None);

        Assert.Single(rows);
        Assert.StartsWith("  1 │", rows[0]);
    }

    [Theory]
    [InlineData(9, "  9 │")]
    [InlineData(42, " 42 │")]
    [InlineData(100, "100 │")]
    public void RenderLine_RightAlignsTheGutterNumber(int lineNumber, string expected)
    {
        var rows = TuiCodeHighlight.RenderLine("x", lineNumber, 40, TuiColorLevel.None);

        Assert.StartsWith(expected, Assert.Single(rows));
    }

    [Fact]
    public void RenderLine_EmptyLine_KeepsTheGutterOnly()
    {
        var rows = TuiCodeHighlight.RenderLine(string.Empty, 3, 40, TuiColorLevel.None);

        Assert.Equal("  3 │ ", Assert.Single(rows));
    }

    [Fact]
    public void RenderLine_NoColorLevel_EmitsNoAnsi()
    {
        var rows = TuiCodeHighlight.RenderLine("if (x == \"y\" + 12) { } // note", 1, 60, TuiColorLevel.None);

        Assert.All(rows, row => Assert.DoesNotContain('\u001b', row));
    }

    [Fact]
    public void RenderLine_TrueColor_PaintsKeywordsStringsCommentsAndNumbers()
    {
        const TuiColorLevel level = TuiColorLevel.TrueColor;

        var rows = TuiCodeHighlight.RenderLine("if (x == \"y\" + 12) { } // note", 1, 60, level);
        var row = Assert.Single(rows);

        using (TuiPalette.PinLevel(level))
        {
            Assert.Contains(TuiPalette.Paint("if", TuiToken.Brand2), row);
            Assert.Contains(TuiPalette.Paint("\"y\"", TuiToken.Ok), row);
            Assert.Contains(TuiPalette.Paint("12", TuiToken.Brand), row);
            Assert.Contains(TuiPalette.Paint("// note", TuiToken.Meta), row);
        }
    }

    [Fact]
    public void RenderLine_BlockComment_TintsOnlyTheComment()
    {
        const TuiColorLevel level = TuiColorLevel.TrueColor;

        var rows = TuiCodeHighlight.RenderLine("/* note */ var x;", 1, 40, level);
        var row = Assert.Single(rows);

        using (TuiPalette.PinLevel(level))
        {
            Assert.Contains(TuiPalette.Paint("/* note */", TuiToken.Meta), row);
            Assert.Contains(TuiPalette.Paint("var", TuiToken.Brand2), row);
        }
    }

    [Fact]
    public void RenderLine_UnterminatedBlockComment_TintsToTheEndOfTheLine()
    {
        const TuiColorLevel level = TuiColorLevel.TrueColor;

        var rows = TuiCodeHighlight.RenderLine("var x; /* to the end", 1, 60, level);
        var row = Assert.Single(rows);

        using (TuiPalette.PinLevel(level))
        {
            Assert.Contains(TuiPalette.Paint("var", TuiToken.Brand2), row);
            Assert.Contains(TuiPalette.Paint("/* to the end", TuiToken.Meta), row);
        }
    }

    [Fact]
    public void RenderLine_CommentMarkerInsideAString_StaysAString()
    {
        const TuiColorLevel level = TuiColorLevel.TrueColor;

        var rows = TuiCodeHighlight.RenderLine("var u = \"http://x\"; // tail", 1, 60, level);
        var row = Assert.Single(rows);

        using (TuiPalette.PinLevel(level))
        {
            Assert.Contains(TuiPalette.Paint("\"http://x\"", TuiToken.Ok), row);
            Assert.Contains(TuiPalette.Paint("// tail", TuiToken.Meta), row);
        }
    }

    [Theory]
    [InlineData(TuiColorLevel.Basic16)]
    [InlineData(TuiColorLevel.Palette256)]
    [InlineData(TuiColorLevel.TrueColor)]
    public void RenderLine_ColorLevels_PaintThroughThePalette(TuiColorLevel level)
    {
        var rows = TuiCodeHighlight.RenderLine("if (x == \"y\") { } // c", 1, 60, level);
        var row = Assert.Single(rows);

        using (TuiPalette.PinLevel(level))
        {
            Assert.Contains(TuiPalette.Code(TuiToken.Brand2) + "if", row);
            Assert.Contains(TuiPalette.Code(TuiToken.Ok) + "\"y\"", row);
            Assert.Contains(TuiPalette.Code(TuiToken.Meta) + "// c", row);
        }

        Assert.True(TuiText.VisibleWidth(row) <= 60);
    }

    [Fact]
    public void RenderLine_RestoresPaletteLevelAfterRendering()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            _ = TuiCodeHighlight.RenderLine("if", 1, 40, TuiColorLevel.None);

            Assert.Equal(TuiColorLevel.TrueColor, TuiPalette.Level);
        }
    }

    [Fact]
    public void RenderLine_LongLine_RetainsSourceForViewportWrapping()
    {
        var rows = TuiCodeHighlight.RenderLine(new string('a', 120), 1, 40, TuiColorLevel.None);
        var row = Assert.Single(rows);

        Assert.EndsWith(new string('a', 120), row);
        Assert.DoesNotContain("…", row);
    }

    [Fact]
    public void RenderLine_NullLine_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => TuiCodeHighlight.RenderLine(null!, 1, 40, TuiColorLevel.None));
    }

    [Fact]
    public void PaletteRoles_PanelAndBorder_TrackTheRequestedLevel()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            Assert.Equal("x", TuiPalette.Panel("x"));
            Assert.Equal("x", TuiPalette.Border("x"));
        }

        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            Assert.Equal(TuiPalette.Code(TuiToken.Bright) + "x" + TuiPalette.Reset, TuiPalette.Panel("x"));
            Assert.Equal(TuiPalette.Code(TuiToken.Line) + "x" + TuiPalette.Reset, TuiPalette.Border("x"));
        }
    }
}
