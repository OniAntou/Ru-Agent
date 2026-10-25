using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class StreamingMarkdownProjectorTests
{
    [Theory]
    [InlineData("first soft line\nsecond **bold** line\n\n# Heading\n- list `code`\n> quote\n")]
    [InlineData("paragraph\n```cs\nvar x = 1;\nreturn x;\n```\nfinal **text**")]
    [InlineData("a **bold\ncontinues** here\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n")]
    [InlineData("1. list\n1234. prose\n----\nafter\n")]
    public void Chunking_does_not_change_committed_blocks_or_styles(string text)
    {
        var expected = Reference(text);
        foreach (var size in new[] { 1, 3, 17, 1024 })
        {
            var rows = new List<TranscriptLine>();
            var markdown = new TuiMarkdown { HighlightCode = true };
            var projector = new StreamingMarkdownProjector(markdown, _ => { }, rows.AddRange);
            for (var offset = 0; offset < text.Length; offset += size)
                projector.Append(text.Substring(offset, Math.Min(size, text.Length - offset)), 80);
            projector.Flush(80);
            Assert.Equal(Flatten(expected), Flatten(rows));
        }
    }

    [Fact]
    public void Reset_discards_partial_paragraph_and_fence_is_not_advanced_by_preview()
    {
        var rows = new List<TranscriptLine>();
        var markdown = new TuiMarkdown { HighlightCode = true };
        var projector = new StreamingMarkdownProjector(markdown, _ => { }, rows.AddRange);
        projector.Append("```", 80);
        Assert.False(markdown.InFence);
        projector.Reset();
        projector.Append("replacement", 80);
        projector.Flush(80);
        Assert.Equal("replacement", Assert.Single(rows).Text);
    }

    private static IReadOnlyList<TranscriptLine> Reference(string text)
    {
        var markdown = new TuiMarkdown { HighlightCode = true };
        var paragraph = new List<string>();
        var rows = new List<TranscriptLine>();
        void FlushParagraph()
        {
            if (paragraph.Count > 0) rows.AddRange(markdown.RenderParagraphPresentation(string.Join(' ', paragraph)));
            paragraph.Clear();
        }
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index == lines.Length - 1 && lines[index].Length == 0) break;
            if (markdown.Classify(lines[index]) == TuiMarkdown.TuiLineKind.Paragraph) paragraph.Add(lines[index]);
            else { FlushParagraph(); rows.AddRange(markdown.RenderPresentation(lines[index], 80)); }
        }
        FlushParagraph();
        return rows;
    }

    private static IEnumerable<(char, TuiToken, bool, bool)> Flatten(IEnumerable<TranscriptLine> rows) =>
        rows.SelectMany(row => row.Runs.SelectMany(run => run.Text.Select(c => (c, run.Token, run.Bold, run.Dim)))
            .Concat(row.ContinuesPrevious ? [] : new[] { ('\n', TuiToken.Ink, false, false) }));
}
