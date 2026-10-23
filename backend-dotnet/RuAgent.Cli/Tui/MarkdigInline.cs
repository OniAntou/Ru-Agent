using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace RuAgent.Cli.Tui;

/// <summary>
/// Inline Markdown through Markdig's CommonMark parser.
///
/// The previous inline pass was a hand-written scanner; it handled bold, inline code,
/// strikethrough and link labels, but not nesting, escapes inside emphasis, autolinks or
/// image syntax. A real parser removes that class of edge case: a fragment is parsed as a
/// document and its first leaf's inline tree is mapped to styled runs, so delimiters that
/// the scanner used to mis-pair simply cannot pair.
/// </summary>
internal static class MarkdigInline
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    private readonly record struct RunStyle(TuiToken Token, bool Bold, bool Dim);

    public static IReadOnlyList<TranscriptRun> Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var document = Markdown.Parse(text, Pipeline);
        var inline = FirstInline(document);
        if (inline is null)
        {
            return [new TranscriptRun(text)];
        }

        var runs = new List<TranscriptRun>();
        Append(inline, runs, new RunStyle(TuiToken.Ink, Bold: false, Dim: false));
        return runs.Count == 0 ? [new TranscriptRun(string.Empty)] : runs;
    }

    private static ContainerInline? FirstInline(ContainerBlock root)
    {
        foreach (var block in root)
        {
            if (block is LeafBlock { Inline: { } leaf })
            {
                return leaf;
            }

            if (block is ContainerBlock container && FirstInline(container) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static void Append(Inline? inline, List<TranscriptRun> runs, RunStyle style)
    {
        for (; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Add(runs, literal.Content.ToString(), style);
                    break;
                case CodeInline code:
                    Add(runs, code.Content.ToString(), style with { Token = TuiToken.Ok, Bold = false, Dim = false });
                    break;
                case EmphasisInline emphasis:
                    Append(emphasis.FirstChild, runs, StyleFor(emphasis, style));
                    break;
                case LinkInline link:
                    // The label is what a terminal can show; the URL stays out of the row so
                    // a long link cannot push the prose off the viewport.
                    Append(link.FirstChild, runs, style with { Token = TuiToken.Brand });
                    break;
                case AutolinkInline autolink:
                    Add(runs, autolink.Url, style with { Token = TuiToken.Brand });
                    break;
                case LineBreakInline:
                    Add(runs, " ", style);
                    break;
                case HtmlInline:
                    break;
                case ContainerInline container:
                    Append(container.FirstChild, runs, style);
                    break;
            }
        }
    }

    private static RunStyle StyleFor(EmphasisInline emphasis, RunStyle style) => emphasis.DelimiterChar switch
    {
        // Strikethrough and subscript/superscript extras: dim is the closest a terminal
        // gets without inventing a new attribute for each.
        '~' or '^' or '+' or '=' => style with { Dim = true },
        _ when emphasis.DelimiterCount >= 2 => style with { Token = TuiToken.Bright, Bold = true },
        _ => style with { Dim = true },
    };

    private static void Add(List<TranscriptRun> runs, string text, RunStyle style)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (runs.Count > 0 && runs[^1] is { } last
            && last.Token == style.Token && last.Bold == style.Bold && last.Dim == style.Dim)
        {
            runs[^1] = last with { Text = last.Text + text };
            return;
        }

        runs.Add(new TranscriptRun(text, style.Token, style.Bold, style.Dim));
    }
}
