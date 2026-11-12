using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiDataToneExtensionTests
{
    [Theory]
    [InlineData("app.js")]
    [InlineData("server.ts")]
    [InlineData("App.tsx")]
    [InlineData("Button.jsx")]
    [InlineData("index.html")]
    [InlineData("main.css")]
    [InlineData("index.d.ts")]
    [InlineData("app.module.css")]
    [InlineData("APP.JS")]
    [InlineData("SERVER.TS")]
    [InlineData("APP.TSX")]
    [InlineData("BUTTON.JSX")]
    [InlineData("INDEX.HTML")]
    [InlineData("MAIN.CSS")]
    public void Web_source_filenames_share_the_existing_file_tone(string file)
    {
        var text = $"Edit src/{file} first.";
        var spans = TgAnsiPaint.Decorate(text, null);
        Assert.NotNull(spans);
        Assert.Equal(text, string.Concat(spans.Select(span => span.Text)));
        Assert.Contains(spans, span => span.Text == file && span.Attr == TgPalette.Attr(TuiToken.Brand2));

        IReadOnlyList<TranscriptRun> source = [new("Edit src/"), new(file), new(" first.")];
        var runs = TuiDataTone.Decorate(source);
        Assert.Equal(text, string.Concat(runs.Select(run => run.Text)));
        Assert.Contains(runs, run => run.Text == file && run.Token == TuiToken.Brand2);

        string[] table = ["File | Status", "--- | ---", $"{file} | plain", $"`{file}` | code", $"**{file}** | bold"];
        Assert.True(TuiMarkdownTable.TryRenderPresentation(table, 0, 60, out _, out var rows));
        var cellRuns = rows.SelectMany(row => row.Runs).Where(run => run.Text == file).ToArray();
        Assert.Contains(cellRuns, run => run.Token == TuiToken.Brand2 && !run.Bold);
        Assert.Contains(cellRuns, run => run.Token == TuiToken.Ok && !run.Bold);
        Assert.Contains(cellRuns, run => run.Token == TuiToken.Bright && run.Bold);
    }

    [Theory]
    [InlineData("app.jsbackup")]
    [InlineData("App.tsxbackup")]
    [InlineData("Button.jsxbackup")]
    [InlineData("index.htmlbackup")]
    [InlineData("main.cssbackup")]
    [InlineData("javascript typescript html css")]
    public void Unsupported_suffixes_and_language_names_stay_plain(string text)
    {
        Assert.Null(TgAnsiPaint.Decorate(text, null));
        IReadOnlyList<TranscriptRun> source = [new(text)];
        Assert.Same(source, TuiDataTone.Decorate(source));
    }
}
