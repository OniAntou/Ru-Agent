using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// The inline pass runs through Markdig, so the cases a hand-written scanner could not pair
/// are covered here: nesting, links, autolinks and inline code.
/// </summary>
public sealed class MarkdigInlineTests
{
    private const char Tick = (char)96;

    private static IReadOnlyList<TranscriptRun> Runs(string text) =>
        TuiMarkdown.PresentationInline(text);

    private static string Text(IReadOnlyList<TranscriptRun> runs) =>
        string.Concat(runs.Select(run => run.Text));

    [Fact]
    public void Nested_emphasis_keeps_both_weights()
    {
        var runs = Runs("**bold *italic* bold**");

        Assert.Equal("bold italic bold", Text(runs));
        var italic = Assert.Single(runs, run => run.Text.Trim() == "italic");
        Assert.True(italic.Bold);
        Assert.True(italic.Dim);
        Assert.True(runs[0].Bold);
        Assert.False(runs[0].Dim);
    }

    [Fact]
    public void Link_keeps_the_label_and_drops_the_url()
    {
        var runs = Runs("[docs](https://example.com/very/long/path)");

        Assert.Equal("docs", Text(runs));
        Assert.Equal(TuiToken.Brand, Assert.Single(runs).Token);
    }

    [Fact]
    public void Autolink_shows_the_url()
    {
        var runs = Runs("<https://example.com>");

        Assert.Equal("https://example.com", Text(runs));
        Assert.Equal(TuiToken.Brand, Assert.Single(runs).Token);
    }

    [Fact]
    public void Strikethrough_is_dimmed()
    {
        var runs = Runs("~~gone~~");

        Assert.Equal("gone", Text(runs));
        Assert.True(Assert.Single(runs).Dim);
    }

    [Fact]
    public void Inline_code_keeps_its_own_token()
    {
        var runs = Runs("run " + Tick + "dotnet test" + Tick + " now");

        Assert.Equal("run dotnet test now", Text(runs));
        Assert.Contains(runs, run => run.Text == "dotnet test" && run.Token == TuiToken.Ok);
    }

    [Fact]
    public void Unmatched_markers_stay_literal()
    {
        Assert.Equal("a **b", Text(Runs("a **b")));
    }
}
