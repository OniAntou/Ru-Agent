using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiToolCardTests
{
    [Fact]
    public void Render_DiffHunk_MarksAddedAndRemovedRows()
    {
        var rows = TuiToolCard.Render(
            title: "edited app.cs",
            lines: ["- old line", "+ new line", "  context"],
            width: 60,
            level: TuiColorLevel.None);

        Assert.Equal(6, rows.Count);
        Assert.Contains("edited app.cs", rows[1]);
        Assert.Contains("+ new line", rows[3]);
    }

    [Fact]
    public void Render_RoundedBorder_FramesEveryRowAtExactWidth()
    {
        var rows = TuiToolCard.Render(
            title: "read Program.cs",
            lines: ["@@ -1,3 +1,4 @@", "- old", "+ new", "  context"],
            width: 80,
            level: TuiColorLevel.TrueColor);

        Assert.All(rows, row => Assert.Equal(80, TuiText.VisibleWidth(row)));
        Assert.StartsWith("╭", TuiText.Strip(rows[0]));
        Assert.EndsWith("╮", TuiText.Strip(rows[0]));
        Assert.StartsWith("╰", TuiText.Strip(rows[^1]));
        Assert.EndsWith("╯", TuiText.Strip(rows[^1]));
    }

    [Fact]
    public void Render_NoBodyLines_ReturnsBordersAndTitleOnly()
    {
        var rows = TuiToolCard.Render(
            title: "read file",
            lines: [],
            width: 60,
            level: TuiColorLevel.None);

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void Render_LongLine_TruncatesToCardWidthWithoutWrapping()
    {
        var rows = TuiToolCard.Render(
            title: "read file",
            lines: [new string('a', 200)],
            width: 40,
            level: TuiColorLevel.None);

        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.Equal(40, TuiText.VisibleWidth(row)));
        Assert.Contains("…", rows[2]);
    }

    [Fact]
    public void Render_WideCharacters_KeepsRowsWidthExact()
    {
        var rows = TuiToolCard.Render(
            title: "đã sửa 文件.cs 你好",
            lines: ["+ 你好 世界", "- さようなら"],
            width: 40,
            level: TuiColorLevel.None);

        Assert.All(rows, row => Assert.Equal(40, TuiText.VisibleWidth(row)));
    }

    [Fact]
    public void Render_BelowMinimumWidth_ClampsRowsToMinimumCardWidth()
    {
        var rows = TuiToolCard.Render(
            title: "read file",
            lines: ["+ new"],
            width: 4,
            level: TuiColorLevel.None);

        Assert.All(rows, row => Assert.Equal(TuiToolCard.MinWidth, TuiText.VisibleWidth(row)));
    }

    [Fact]
    public void Render_NoColorLevel_EmitsNoAnsi()
    {
        var rows = TuiToolCard.Render(
            title: "edited app.cs",
            lines: ["- old line", "+ new line", "  context"],
            width: 60,
            level: TuiColorLevel.None);

        Assert.All(rows, row => Assert.DoesNotContain('\u001b', row));
    }

    [Theory]
    [InlineData(TuiColorLevel.Basic16)]
    [InlineData(TuiColorLevel.Palette256)]
    [InlineData(TuiColorLevel.TrueColor)]
    public void Render_ColorLevels_PaintAddedRowsGreenAndRemovedRowsRed(TuiColorLevel level)
    {
        var rows = TuiToolCard.Render(
            title: "edited app.cs",
            lines: ["- old line", "+ new line", "  context"],
            width: 60,
            level: level);

        using (TuiPalette.PinLevel(level))
        {
            Assert.Contains(TuiPalette.Code(TuiToken.Ok) + "+ new line", rows[3]);
            Assert.Contains(TuiPalette.Code(TuiToken.Err) + "- old line", rows[2]);
            Assert.DoesNotContain(TuiPalette.Code(TuiToken.Err), rows[3]);
        }

        Assert.All(rows, row => Assert.Equal(60, TuiText.VisibleWidth(row)));
    }

    [Fact]
    public void Render_RestoresPaletteLevelAfterRendering()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            _ = TuiToolCard.Render(
                title: "edited app.cs",
                lines: ["+ new"],
                width: 60,
                level: TuiColorLevel.None);

            Assert.Equal(TuiColorLevel.TrueColor, TuiPalette.Level);
        }
    }

    [Fact]
    public void Render_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(
            () => TuiToolCard.Render(title: null!, lines: ["+ new"], width: 60, level: TuiColorLevel.None));
        Assert.Throws<ArgumentNullException>(
            () => TuiToolCard.Render(title: "read file", lines: null!, width: 60, level: TuiColorLevel.None));
    }
}
