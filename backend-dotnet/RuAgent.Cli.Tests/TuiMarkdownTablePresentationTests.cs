using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiMarkdownTablePresentationTests
{
    [Theory]
    [InlineData(80)]
    [InlineData(40)]
    [InlineData(18)]
    public void Data_tone_survives_wrapping_inside_a_cell(int width)
    {
        const string file = "ThisIsALongDocumentName.md";
        const string number = "140,600,700,800,900,100,200";
        string[] source = ["File | Value", "--- | ---", $"{file} | {number}"];
        Assert.True(TuiMarkdownTable.TryRenderPresentation(source, 0, width, out var consumed, out var rows));
        Assert.Equal(3, consumed);
        Assert.Equal(file, string.Concat(rows.SelectMany(row => row.Runs).Where(run => run.Token == TuiToken.Brand2).Select(run => run.Text)));
        Assert.Equal(number, string.Concat(rows.SelectMany(row => row.Runs).Where(run => run.Token == TuiToken.Brand).Select(run => run.Text)));
        Assert.All(rows, row => Assert.InRange(TuiText.VisibleWidth(row.Text), 0, width));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(40)]
    [InlineData(18)]
    public void Repeated_values_keep_their_own_cell_styles(int width)
    {
        string[] source = ["First | Second", ":---: | ---:", "**same** | `same`", "same | [same](https://example.test)"];
        Assert.True(TuiMarkdownTable.TryRenderPresentation(source, 0, width, out _, out var rows));
        var repeated = rows.SelectMany(row => row.Runs).Where(run => run.Text.Contains("same", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, repeated.Length);
        Assert.True(repeated[0].Bold);
        Assert.Equal(TuiToken.Ok, repeated[1].Token);
        Assert.Equal(TuiToken.Ink, repeated[2].Token);
        Assert.Equal(TuiToken.Brand, repeated[3].Token);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(40)]
    [InlineData(18)]
    public void Unicode_and_collapsed_whitespace_keep_paint_aligned(int width)
    {
        string[] source = ["First | Second", "--- | ---", "**alpha   beta😀中文** | `e\u0301cho`", "a\\|b | `x|y`"];
        Assert.True(TuiMarkdownTable.TryRenderPresentation(source, 0, width, out _, out var rows));
        var bold = string.Concat(rows.SelectMany(row => row.Runs).Where(run => run.Bold).Select(run => run.Text));
        Assert.Equal("alphabeta😀中文", bold.Replace(" ", ""));
        Assert.Equal("e\u0301chox|y", string.Concat(rows.SelectMany(row => row.Runs).Where(run => run.Token == TuiToken.Ok).Select(run => run.Text)));
        Assert.DoesNotContain(rows, row => row.Text.Contains('\ufffd') || row.Text.Contains('\u001b'));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(40)]
    [InlineData(18)]
    public void Incomplete_unicode_uses_the_display_replacement_without_losing_style(int width)
    {
        string[] source = ["First | Second", "--- | ---", "**broken\ud800** | good"];
        Assert.True(TuiMarkdownTable.TryRenderPresentation(source, 0, width, out _, out var rows));
        Assert.Equal("broken\ufffd", string.Concat(rows.SelectMany(row => row.Runs).Where(run => run.Bold).Select(run => run.Text)));
    }
}
