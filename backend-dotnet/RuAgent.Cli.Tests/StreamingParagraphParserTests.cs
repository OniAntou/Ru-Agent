using RuAgent.Cli.Tui;
using Xunit;
using Xunit.Abstractions;

namespace RuAgent.Cli.Tests;

public sealed class StreamingParagraphParserTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("plain **bold** and _italic_ with `code` tail")]
    [InlineData("nested **bold _inside_ bold** and ~~gone~~ tail")]
    [InlineData("a [link with words](https://example.test/a) and ![image](a.png) tail")]
    [InlineData("www.example.test and https://example.test/path?x=1 tail")]
    [InlineData("a \\*literal\\* &amp; entity <span>HTML</span> tail")]
    [InlineData("a_b_c and **unclosed _nested words trailing")]
    [InlineData("code ``a ` tick`` and [unfinished label tail")]
    public void Every_delta_partition_matches_commonmark_including_checkpoint_context(string suffix)
    {
        var prefix = string.Concat(Enumerable.Repeat("settled text ", 340));
        foreach (var chunkSize in new[] { 1, 7, 137, 4096, 20000 })
        {
            var parser = new StreamingParagraphParser();
            parser.Append(prefix);
            parser.Preview();
            for (var index = 0; index < suffix.Length; index += chunkSize)
            {
                parser.Append(suffix.Substring(index, Math.Min(chunkSize, suffix.Length - index)));
                AssertRuns(MarkdigInline.Parse(prefix + suffix[..Math.Min(index + chunkSize, suffix.Length)]), parser.Preview());
            }
        }
    }

    [Theory]
    [InlineData("**", "**")]
    [InlineData("`", "`")]
    [InlineData("[", "](https://example.test)")]
    public void Late_closer_can_restyle_the_entire_unresolved_suffix(string open, string close)
    {
        var prefix = string.Concat(Enumerable.Repeat("known text ", 450));
        var unresolved = open + string.Concat(Enumerable.Repeat("long text ", 800));
        var parser = new StreamingParagraphParser();
        parser.Append(prefix);
        parser.Preview();
        parser.Append(unresolved);
        AssertRuns(MarkdigInline.Parse(prefix + unresolved), parser.Preview());
        parser.Append(close + " final tail");
        AssertRuns(MarkdigInline.Parse(prefix + unresolved + close + " final tail"), parser.Preview());
    }

    [Fact]
    public void Unchanged_prefix_objects_and_preview_are_reused_and_reset_drops_old_state()
    {
        var parser = new StreamingParagraphParser();
        parser.Append(string.Concat(Enumerable.Repeat("stable words ", 500)) + "tail");
        var first = parser.Preview();
        Assert.True(parser.SettledChunkCount > 0);
        var parsed = parser.ParsedCharacters;
        Assert.Same(first, parser.Preview());
        Assert.Equal(parsed, parser.ParsedCharacters);
        parser.Append(" new tail");
        Assert.Same(first[0], parser.Preview()[0]);
        parser.Reset();
        parser.Append("replacement");
        AssertRuns(MarkdigInline.Parse("replacement"), parser.Preview());
        Assert.Equal(0, parser.SettledChunkCount);
    }

    [Fact]
    public void Long_stream_parses_only_the_mutable_tail_instead_of_the_growing_history()
    {
        var text = string.Concat(Enumerable.Repeat("normal words **bold text** and `code words` end. ", 3000));
        var parser = new StreamingParagraphParser();
        long baselineCharacters = 0;
        for (var offset = 0; offset < text.Length; offset += 257)
        {
            parser.Append(text.Substring(offset, Math.Min(257, text.Length - offset)));
            parser.Preview();
            baselineCharacters += Math.Min(offset + 257, text.Length);
        }
        Assert.True(parser.SettledChunkCount > 20);
        Assert.True(parser.ParsedCharacters < 16L * text.Length,
            $"Parsed {parser.ParsedCharacters} characters for {text.Length} source characters.");
        AssertRuns(MarkdigInline.Parse(text), parser.Preview());
        output.WriteLine($"Source={text.Length}; tail-parser={parser.ParsedCharacters}; whole-prefix baseline={baselineCharacters}; chunks={parser.SettledChunkCount}");
    }

    [Theory]
    [InlineData("`", "`")]
    [InlineData("[", "](https://example.test)")]
    [InlineData("**", "**")]
    public void Checkpoints_never_split_whitespace_inside_a_markdown_construct(string open, string close)
    {
        var text = open + string.Concat(Enumerable.Repeat("words inside ", 600)) + close;
        var parser = new StreamingParagraphParser();
        for (var offset = 0; offset < text.Length; offset += 401)
        {
            parser.Append(text.Substring(offset, Math.Min(401, text.Length - offset)));
            parser.Preview();
        }
        Assert.Equal(0, parser.SettledChunkCount);
        AssertRuns(MarkdigInline.Parse(text), parser.Preview());
    }

    [Fact]
    public void Mixed_late_syntax_matches_whole_parsing_at_every_snapshot()
    {
        var random = new Random(1952);
        string[] pieces = ["plain ", "**bold** ", "*", "_", "`", "~~", "[", "]", "(https://example.test) ", "&amp; ", "\\* ", "Việt😀 ", "\t", "<span>", "</span>"];
        for (var sample = 0; sample < 12; sample++)
        {
            var source = new System.Text.StringBuilder(string.Concat(Enumerable.Repeat("settled **words** ", 280)));
            var parser = new StreamingParagraphParser();
            parser.Append(source.ToString());
            parser.Preview();
            for (var index = 0; index < 32; index++)
            {
                var next = pieces[random.Next(pieces.Length)];
                source.Append(next);
                parser.Append(next);
                AssertRuns(MarkdigInline.Parse(source.ToString()), parser.Preview());
            }
        }
    }

    private static void AssertRuns(IReadOnlyList<TranscriptRun> expected, IReadOnlyList<TranscriptLine> actual) =>
        Assert.Equal(Flatten(expected), Flatten(actual.SelectMany(row => row.Runs)));

    private static IEnumerable<(char Char, TuiToken Token, bool Bold, bool Dim)> Flatten(IEnumerable<TranscriptRun> runs) =>
        runs.SelectMany(run => run.Text.Select(c => (c, run.Token, run.Bold, run.Dim)));
}
