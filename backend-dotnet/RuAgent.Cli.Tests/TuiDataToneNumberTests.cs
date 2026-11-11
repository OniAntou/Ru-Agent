using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiDataToneNumberTests
{
    [Theory]
    [InlineData("5%")]
    [InlineData("2.86%")]
    [InlineData("-2,5 %")]
    [InlineData("+12.5%")]
    [InlineData("0.25")]
    [InlineData("3,14")]
    [InlineData("-0.25")]
    [InlineData("−0.25")]
    [InlineData("1,234.56")]
    [InlineData("1.234,56")]
    [InlineData("140,600")]
    [InlineData("$10")]
    [InlineData("$1,234.56")]
    [InlineData("-$12.50")]
    [InlineData("$-12.50")]
    [InlineData("€ 1.234,56")]
    [InlineData("£5.50")]
    [InlineData("¥500")]
    [InlineData("₫100000")]
    [InlineData("1.234,56 EUR")]
    [InlineData("100000 VND")]
    [InlineData("100.000 đ")]
    [InlineData("100000₫")]
    [InlineData("USD 42.50")]
    [InlineData("42.50 usd")]
    [InlineData("2026-11-08")]
    [InlineData("08/11/2026")]
    [InlineData("8/11/2026")]
    [InlineData("08-11-2026")]
    [InlineData("08.11.2026")]
    [InlineData("29/02/2024")]
    public void Numeric_forms_keep_one_tone_in_prose_and_wrapped_tables(string value)
    {
        var text = $"Value {value} here.";
        var spans = TgAnsiPaint.Decorate(text, null);
        Assert.NotNull(spans);
        Assert.Equal(text, string.Concat(spans.Select(span => span.Text)));
        Assert.Equal(value, string.Concat(spans.Where(span => span.Attr == TgPalette.Attr(TuiToken.Brand)).Select(span => span.Text)));

        IReadOnlyList<TranscriptRun> source = [new("Value "), new(value), new(" here.")];
        var runs = TuiDataTone.Decorate(source);
        Assert.Equal(text, string.Concat(runs.Select(run => run.Text)));
        Assert.Equal(value, string.Concat(runs.Where(run => run.Token == TuiToken.Brand).Select(run => run.Text)));

        foreach (var width in new[] { 60, 18 })
        {
            string[] table = ["Name | Value", "--- | ---", $"item | {value}"];
            Assert.True(TuiMarkdownTable.TryRenderPresentation(table, 0, width, out _, out var rows));
            var colored = string.Concat(rows.SelectMany(row => row.Runs).Where(run => run.Token == TuiToken.Brand).Select(run => run.Text));
            Assert.Equal(value.Replace(" ", ""), colored.Replace(" ", ""));
        }
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.10")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3")]
    [InlineData("12,34,56")]
    [InlineData("31/02/2026")]
    [InlineData("29/02/2025")]
    [InlineData("2026-13-40")]
    [InlineData("31.02.2026")]
    [InlineData("abc2.86xyz")]
    [InlineData("30 tools")]
    public void Unsupported_or_invalid_forms_stay_plain(string text)
    {
        Assert.Null(TgAnsiPaint.Decorate(text, null));
        Assert.Empty(TuiDataTone.FindMatches(text));
    }

    [Theory]
    [InlineData("release-1.2.js")]
    [InlineData("report-2026-11-08.md")]
    [InlineData("data-1.234.json")]
    [InlineData("1.2.ts")]
    [InlineData("2026-11-08.md")]
    public void Filename_and_numeric_candidates_never_duplicate_visible_text(string text)
    {
        var spans = TgAnsiPaint.Decorate(text, null);
        Assert.NotNull(spans);
        Assert.Equal(text, string.Concat(spans.Select(span => span.Text)));
        var matches = TuiDataTone.FindMatches(text);
        Assert.All(matches.Zip(matches.Skip(1)), pair => Assert.True(pair.First.End <= pair.Second.Start));
        Assert.DoesNotContain(matches, match => match.Token == TuiToken.Brand);
    }

    [Fact]
    public void Explicit_markdown_styling_wins_over_all_numeric_forms()
    {
        var runs = TuiDataTone.Decorate(MarkdigInline.Parse("**2.5%** and `$12.50` and [08/11/2026](https://example.test)"));
        Assert.Contains(runs, run => run.Text == "2.5%" && run.Bold && run.Token == TuiToken.Bright);
        Assert.Contains(runs, run => run.Text == "$12.50" && run.Token == TuiToken.Ok);
        Assert.Contains(runs, run => run.Text == "08/11/2026" && run.Token == TuiToken.Brand);
        Assert.Equal("2.5% and $12.50 and 08/11/2026", string.Concat(runs.Select(run => run.Text)));
    }

    [Fact]
    public void Mixed_numbers_dates_and_versioned_filenames_keep_disjoint_ranges()
    {
        const string text = "Use App-1.2.tsx on 08/11/2026: +2.5%, $-10.50 and 3,14.";
        var matches = TuiDataTone.FindMatches(text);
        Assert.Equal(new[] { "App-1.2.tsx", "08/11/2026", "+2.5%", "$-10.50", "3,14" }, matches.Select(match => text[match.Start..match.End]));
        Assert.All(matches.Zip(matches.Skip(1)), pair => Assert.True(pair.First.End <= pair.Second.Start));
        var spans = TgAnsiPaint.Decorate(text, null);
        Assert.NotNull(spans);
        Assert.Equal(text, string.Concat(spans.Select(span => span.Text)));
    }
}
