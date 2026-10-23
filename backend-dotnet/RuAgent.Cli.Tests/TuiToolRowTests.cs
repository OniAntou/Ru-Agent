using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiToolRowTests
{
    [Fact]
    public void Compose_CompletedRow_ShowsCheckDisplayTargetAndMeta()
    {
        var row = TuiToolRow.Compose(ToolLineStatus.Ok, "Search", "\"homepage\"", "(18 matches)", 80);

        var text = TuiText.Strip(row);
        Assert.StartsWith("  ✓ Search  \"homepage\"", text, StringComparison.Ordinal);
        Assert.EndsWith("(18 matches)", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("View", "→")]
    [InlineData("Read", "→")]
    [InlineData("Docs", "→")]
    [InlineData("Explore", "✱")]
    [InlineData("Search", "✱")]
    [InlineData("Edit", "←")]
    [InlineData("Write", "←")]
    [InlineData("Delete", "←")]
    [InlineData("Terminal", "$")]
    [InlineData("Fetch", "%")]
    [InlineData("Plan", "⚙")]
    [InlineData("Mystery", "⚙")]
    public void Icon_UsesTheOpencodeGlyphVocabulary(string display, string expected) =>
        Assert.Equal(expected, TuiToolRow.Icon(display));

    [Fact]
    public void Compose_FailedRow_PaintsTheErrorTone()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            var row = TuiToolRow.Compose(ToolLineStatus.Failed, "Edit", "app.cs", null, 60);

            Assert.Contains(TuiPalette.Code(TuiToken.Err), row);
            Assert.Contains("  ✗ Edit  app.cs", TuiText.Strip(row), StringComparison.Ordinal);
            Assert.EndsWith("failed", TuiText.Strip(row), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Compose_CompletedRow_PaintsTheOkTone()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            var row = TuiToolRow.Compose(ToolLineStatus.Ok, "Search", "query", "(2 matches)", 60);

            Assert.Contains(TuiPalette.Code(TuiToken.Ok), row);
            Assert.DoesNotContain(TuiPalette.Code(TuiToken.Err), row);
        }
    }

    [Fact]
    public void Compose_LongRow_TruncatesToWidthWithoutWrapping()
    {
        var row = TuiToolRow.Compose(ToolLineStatus.Ok, "View", new string('a', 300), null, 40);

        Assert.True(TuiText.VisibleWidth(row) <= 40);
        Assert.Contains("…", TuiText.Strip(row));
    }

    [Fact]
    public void Compose_BelowMinimumWidth_ClampsUpToTheMinimum()
    {
        var row = TuiToolRow.Compose(ToolLineStatus.Ok, "View", "file", null, 4);

        Assert.True(TuiText.VisibleWidth(row) <= TuiToolRow.MinWidth);
    }

    [Fact]
    public void Compose_NoColorLevel_EmitsNoAnsi()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            var row = TuiToolRow.Compose(ToolLineStatus.Ok, "Search", "q", "(1 match)", 60);

            Assert.DoesNotContain('\u001b', row);
        }
    }

    [Fact]
    public void Compose_BlankMeta_LeavesItOff()
    {
        var row = TuiText.Strip(TuiToolRow.Compose(ToolLineStatus.Ok, "View", "app.cs", null, 60));

        Assert.DoesNotContain("(", row, StringComparison.Ordinal);
        Assert.EndsWith("app.cs", row, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_BlankDisplay_Throws() =>
        Assert.Throws<ArgumentException>(() => TuiToolRow.Compose(ToolLineStatus.Ok, "  ", null, null, 60));
}
