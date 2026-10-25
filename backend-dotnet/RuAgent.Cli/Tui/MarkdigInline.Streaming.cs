using Markdig;
using Markdig.Syntax.Inlines;

namespace RuAgent.Cli.Tui;

internal static partial class MarkdigInline
{
    private static readonly MarkdownPipeline StreamingPipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras().UseAutoLinks().UsePreciseSourceLocation().Build();

    internal static (IReadOnlyList<TranscriptRun> Runs, int StableLength) ParseTail(string text, bool continuation)
    {
        // A tail starts inside an existing paragraph, not at a new block boundary.
        var offset = continuation ? 2 : 0;
        var input = continuation ? "x " + text : text;
        var inline = FirstInline(Markdown.Parse(input, StreamingPipeline));
        if (inline is null) return (text.Length == 0 ? [] : [new TranscriptRun(text)], 0);
        var runs = new List<TranscriptRun>();
        Append(inline, runs, new RunStyle(TuiToken.Ink, false, false));
        var lastNonSpace = input.Length - 1;
        while (lastNonSpace >= offset && char.IsWhiteSpace(input[lastNonSpace])) lastNonSpace--;
        var stable = 0;
        for (var node = inline.FirstChild; node is not null; node = node.NextSibling)
        {
            if (!IsSettled(node, input)) break;
            // A whitespace inside a link label or code span is not a checkpoint.
            if (node is not LiteralInline) continue;
            for (var index = Math.Max(offset, node.Span.Start); index <= node.Span.End && index < lastNonSpace; index++)
                if (char.IsWhiteSpace(input[index])) stable = index + 1 - offset;
        }

        // Keep the last word and all trailing whitespace provisional: URLs, entities
        // and delimiter runs can still grow there. Never freeze an unresolved literal.
        return (SliceRuns(runs, offset, 0), stable);
    }

    private static bool IsSettled(Inline node, string input)
    {
        if (node.Span.Start < 0 || node.Span.End >= input.Length) return false;
        if (node is LiteralInline)
            return input.AsSpan(node.Span.Start, node.Span.Length).IndexOfAny("*_`~^+=[]<>&\\!|") < 0;
        if (node is CodeInline or AutolinkInline) return true;
        if (node is not (EmphasisInline or LinkInline)) return false;
        for (var child = ((ContainerInline)node).FirstChild; child is not null; child = child.NextSibling)
            if (!IsSettled(child, input)) return false;
        return true;
    }

    internal static IReadOnlyList<TranscriptRun> ParseFragment(string text, bool continuation, bool preserveTrailingSpace)
    {
        var runs = Parse((continuation ? "x " : "") + text + (preserveTrailingSpace ? "x" : ""));
        return SliceRuns(runs, continuation ? 2 : 0, preserveTrailingSpace ? 1 : 0);
    }

    private static IReadOnlyList<TranscriptRun> SliceRuns(IReadOnlyList<TranscriptRun> runs, int skip, int trimEnd)
    {
        var length = Math.Max(0, runs.Sum(run => run.Text.Length) - skip - trimEnd);
        var result = new List<TranscriptRun>();
        foreach (var run in runs)
        {
            var from = Math.Min(skip, run.Text.Length);
            skip -= from;
            var take = Math.Min(length, run.Text.Length - from);
            if (take > 0) result.Add(run with { Text = run.Text.Substring(from, take) });
            length -= take;
        }
        return result;
    }
}
