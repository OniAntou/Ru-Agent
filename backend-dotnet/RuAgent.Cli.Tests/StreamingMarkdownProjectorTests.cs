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

    [Fact]
    public void Completed_rows_arrive_before_preview_and_before_the_next_append()
    {
        var events = new List<(bool Preview, IReadOnlyList<TranscriptLine> Rows)>();
        var projector = new StreamingMarkdownProjector(new TuiMarkdown { HighlightCode = true },
            rows => events.Add((true, rows)), rows => events.Add((false, rows)));

        projector.Append("```cs\nfirst\nsecond\nthird", 80);
        Assert.False(events[0].Preview);
        var partial = Assert.Single(events, item => item.Preview);
        Assert.Contains("third", string.Join(' ', partial.Rows.Select(row => row.Text)));
        var previewIndex = events.FindIndex(item => item.Preview);
        Assert.Equal(Flatten(Reference("```cs\nfirst\nsecond\n")),
            Flatten(events.Take(previewIndex).SelectMany(item => item.Rows)));

        // Finish the preview, close the fence, then start another block. Retain callback
        // arrays to catch accidental reuse of a mutable batch after dispatch.
        projector.Append("\n```\n# Next\n", 80);
        projector.Flush(80);
        var committed = events.Where(item => !item.Preview).ToArray();
        Assert.Equal(Flatten(Reference("```cs\nfirst\nsecond\nthird\n```\n# Next\n")),
            Flatten(committed.SelectMany(item => item.Rows)));
        var emptyIndex = Array.FindIndex(committed, item => item.Rows.Count == 0);
        Assert.True(emptyIndex > 0);
        Assert.Contains("third", committed[emptyIndex - 1].Rows[^1].Text);
        Assert.Contains("Next", committed[emptyIndex + 1].Rows[0].Text);
    }

    [Fact]
    public void Wide_code_rows_are_bounded_by_characters_and_do_not_leak_after_reset()
    {
        var batches = new List<IReadOnlyList<TranscriptLine>>();
        var projector = new StreamingMarkdownProjector(new TuiMarkdown { HighlightCode = true }, _ => { }, batches.Add);
        var source = "```\n" + string.Concat(Enumerable.Repeat(new string('x', 5000) + "\n", 100)) + "```\n";
        projector.Append(source, 80);
        projector.Flush(80);
        Assert.Equal(Flatten(Reference(source)), Flatten(batches.SelectMany(rows => rows)));
        Assert.All(batches, rows => Assert.InRange(rows.Sum(row => row.Runs.Sum(run => run.Text.Length)), 0, 16 * 1024));
        var completed = batches.Count;
        projector.Reset();
        projector.Append("new turn", 80);
        projector.Flush(80);
        Assert.Equal("new turn", Assert.Single(batches.Skip(completed).SelectMany(rows => rows)).Text);
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
