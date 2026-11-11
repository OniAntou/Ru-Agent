using System.Runtime.ExceptionServices;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Drawing;
using Xunit;
using Attr = Terminal.Gui.Drawing.Attribute;

namespace RuAgent.Cli.Tests;

public sealed class TgHighlightConsistencyTests
{
    [Theory]
    [InlineData("README.md costs 140,600")]
    [InlineData("- README.md costs 140,600")]
    [InlineData("1. README.md costs 140,600")]
    [InlineData("  - README.md costs 140,600")]
    [InlineData("> README.md costs 140,600")]
    [InlineData("    README.md costs 140,600")]
    public void Prose_layouts_keep_data_tone_in_preview_commit_and_resize(string source)
    {
        WithWriter((view, writer) =>
        {
            var rows = new TuiMarkdown().RenderPresentation(source, 80);
            writer.PreviewPresentation(rows);
            AssertDataTone(view.WrappedLines);
            writer.CommitPresentation(rows);
            foreach (var width in new[] { 80, 44, 30 })
            {
                view.Frame = new(0, 0, width, 12);
                AssertDataTone(view.WrappedLines);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fenced_code_keeps_lexical_styles_instead_of_prose_data_tone(bool highlightCode)
    {
        WithWriter((view, writer) =>
        {
            var markdown = new TuiMarkdown { HighlightCode = highlightCode };
            markdown.RenderPresentation("```txt", 80);
            writer.CommitPresentation(markdown.RenderPresentation("README.md 140,600", 80));
            var row = Assert.Single(view.Lines);
            AssertPaint([row], "README.md", TgPalette.Attr(TuiToken.Ink));
            if (!highlightCode) AssertPaint([row], "140,600", TgPalette.Attr(TuiToken.Ink));
        });
    }

    [Theory]
    [InlineData("**140,600**", "140,600", TuiToken.Bright, true)]
    [InlineData("`README.md`", "README.md", TuiToken.Ok, false)]
    [InlineData("[README.md](https://example.test)", "README.md", TuiToken.Brand, false)]
    public void Explicit_markdown_remains_authoritative_in_quotes_and_lists(
        string markup, string text, TuiToken token, bool bold)
    {
        WithWriter((view, writer) =>
        {
            foreach (var source in new[] { "- " + markup, "> " + markup })
            {
                writer.CommitPresentation(new TuiMarkdown().RenderPresentation(source, 80));
                var expected = TgPalette.Attr(token);
                if (bold) expected = new Attr(expected.Foreground, expected.Background, TextStyle.Bold);
                AssertPaint([view.Lines[^1]], text, expected);
            }
        });
    }

    [Fact]
    public void Tables_preserve_cell_markdown_and_data_tone_after_growth_and_resize()
    {
        WithWriter((view, writer) =>
        {
            var markdown = new TuiMarkdown();
            foreach (var line in new[]
            {
                "| File | Value |",
                "| --- | ---: |",
                "| README.md | 140,600 |",
                "| `config.json` | **240,600** |",
                "| [notes.md](https://example.test) | 340,600 |"
            })
            {
                writer.CommitPresentation(markdown.RenderPresentation(line, 80));
                _ = view.WrappedLines;
            }
            foreach (var width in new[] { 80, 44, 22, 80 })
            {
                view.Frame = new(0, 0, width, 18);
                var rows = view.WrappedLines;
                AssertDataTone(rows);
                AssertPaint(rows, "config.json", TgPalette.Attr(TuiToken.Ok));
                var bright = TgPalette.Attr(TuiToken.Bright);
                AssertPaint(rows, "240,600", new Attr(bright.Foreground, bright.Background, TextStyle.Bold));
                AssertPaint(rows, "notes.md", TgPalette.Attr(TuiToken.Brand));
                Assert.All(rows, row => Assert.Equal(row.Text, string.Concat(row.Spans?.Select(span => span.Text) ?? [row.Text])));
                Assert.DoesNotContain(rows, row => row.Text.Contains("**") || row.Text.Contains('`') || row.Text.Contains('\u001b'));
            }
        });
    }

    [Theory]
    [InlineData("- README.md costs 140,600", false)]
    [InlineData("- README.md costs 140,600", true)]
    [InlineData("> README.md costs 140,600", false)]
    [InlineData("> README.md costs 140,600", true)]
    [InlineData("| File | Value |\n| --- | --- |\n| README.md | 140,600 |\n| `config.json` | **240,600** |", false)]
    [InlineData("| File | Value |\n| --- | --- |\n| README.md | 140,600 |\n| `config.json` | **240,600** |", true)]
    public void Streamed_and_replayed_answers_share_the_same_data_tone(string source, bool replay)
    {
        WithWriter((view, writer) =>
        {
            var surface = new TuiSurface(writer);
            var transcript = new TuiTranscript(surface, new TuiStatusLine(surface)) { FullScreenCards = true, Width = 80 };
            if (replay) transcript.ReplayTurn(null, [source], null, null, TuiTurnOutcome.Completed, null);
            else
            {
                for (var offset = 0; offset < source.Length; offset += 3)
                {
                    transcript.AppendAssistant(source.Substring(offset, Math.Min(3, source.Length - offset)));
                    surface.Flush();
                }
                transcript.Flush();
            }
            AssertDataTone(view.WrappedLines);
            if (source.Contains("config.json", StringComparison.Ordinal))
            {
                AssertPaint(view.WrappedLines, "config.json", TgPalette.Attr(TuiToken.Ok));
                var bright = TgPalette.Attr(TuiToken.Bright);
                AssertPaint(view.WrappedLines, "240,600", new Attr(bright.Foreground, bright.Background, TextStyle.Bold));
            }
        });
    }

    private static void AssertDataTone(IReadOnlyList<TgTranscriptRow> rows)
    {
        AssertPaint(rows, "README.md", TgPalette.Attr(TuiToken.Brand2));
        AssertPaint(rows, "140,600", TgPalette.Attr(TuiToken.Brand));
    }

    private static void AssertPaint(IReadOnlyList<TgTranscriptRow> rows, string text, Attr expected)
    {
        var cells = rows.SelectMany(row => (row.Spans ?? [new TgSpan(row.Text, TgPalette.Attr(TuiToken.Ink))])
            .SelectMany(span => span.Text.Select(ch => (Character: ch, span.Attr)))).ToArray();
        var visible = new string(cells.Select(cell => cell.Character).ToArray());
        var start = visible.IndexOf(text, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing '{text}' in '{visible}'.");
        Assert.All(cells.Skip(start).Take(text.Length), cell => Assert.Equal(expected, cell.Attr));
    }

    private static void WithWriter(Action<TgTranscriptView, TgTranscriptWriter> action)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript);
        Exception? failure = null;
        var ran = false;
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            try { ran = true; window.Transcript.Frame = new(0, 0, 80, 18); action(window.Transcript, writer); }
            catch (Exception error) { failure = error; }
            finally { app.RequestStop(); }
            return false;
        });
        app.Run(window);
        Assert.True(ran);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
