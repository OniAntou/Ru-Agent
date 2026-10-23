namespace RuAgent.Cli.Tui;

public sealed partial class TuiMarkdown
{
    /// <summary>Preview without advancing fence state or consuming a code-line number.</summary>
    public IReadOnlyList<TranscriptLine> PreviewPresentation(string line, int width)
    {
        var trimmed = line.TrimStart();
        if (!InFence && trimmed.Length > 0
            && (FenceChars.StartsWith(trimmed, StringComparison.Ordinal)
                || trimmed.StartsWith(FenceChars, StringComparison.Ordinal))) return [];
        var copy = new TuiMarkdown { HighlightCode = HighlightCode, _fence = _fence, _codeLine = _codeLine };
        return copy.RenderPresentation(line, width);
    }

    /// <summary>
    /// The kind of block a complete raw line starts or continues. The transcript buffers the
    /// paragraph kinds and commits every other kind immediately, which is what lets a reflowed
    /// paragraph replace its preview instead of printing one row per source line.
    /// </summary>
    internal enum TuiLineKind
    {
        Blank,
        Paragraph,
        Heading,
        List,
        Quote,
        Rule,
        Fence,
        Table,
    }

    internal TuiLineKind Classify(string line)
    {
        if (InFence) return TuiLineKind.Fence;
        if (line.Length == 0) return TuiLineKind.Blank;
        if (IsFenceLine(line)) return TuiLineKind.Fence;
        if (TryRule(line, out _)) return TuiLineKind.Rule;
        if (TryQuote(line, out _)) return TuiLineKind.Quote;
        if (TryHeading(line, out _, out _)) return TuiLineKind.Heading;
        if (IsTableCandidate(line)) return TuiLineKind.Table;
        if (TryNestedBullet(line, out _) || IsDeeperBullet(line) || TryBullet(line, out _)
            || TryOrdered(line, out _, out _))
        {
            return TuiLineKind.List;
        }

        return TuiLineKind.Paragraph;
    }

    /// <summary>One reflowed paragraph: soft line breaks become spaces, inline styles stay.</summary>
    public IReadOnlyList<TranscriptLine> RenderParagraphPresentation(string text) =>
        [new(PresentationInline(text))];

    /// <summary>Logical display rows with semantic styles; the screen alone wraps them.</summary>
    public IReadOnlyList<TranscriptLine> RenderPresentation(string line, int width)
    {
        ArgumentNullException.ThrowIfNull(line);
        // The screen needs the raw characters of a table row, including its pipes, before
        // it can recognize and re-render the table. Only an outer-pipe row qualifies: a
        // sentence that merely mentions a pipe (a shell command, a regex) used to lose all
        // of its inline markdown because of this early return.
        if (!InFence && IsTableCandidate(line)) return [TranscriptLine.Plain(line)];
        if (InFence)
        {
            if (IsFenceLine(line)) { _fence = string.Empty; return []; }
            var code = new TranscriptRunBuilder();
            if (HighlightCode)
                return [code.Append($"{++_codeLine,3} ", TuiToken.Line).Append("│ ", TuiToken.Line)
                    .Append(TuiCodeHighlight.Tokenize(line)).Line()];
            return [code.Append(Gutter, TuiToken.Line).Append(line, TuiToken.Ink).Line()];
        }
        if (IsFenceLine(line))
        {
            _fence = FenceChars;
            _codeLine = 0;
            var info = line.Trim().TrimStart('`').Trim();
            if (info.Length == 0 || width <= 4) return [];
            var head = "  ── " + ChatDisplayText.Sanitize(info, 32) + " ";
            return [new TranscriptRunBuilder().Append(head, TuiToken.Meta)
                .Append(new string('─', Math.Max(0, width - TuiText.VisibleWidth(head))), TuiToken.Line).Line()];
        }
        if (line.Length == 0) return [TranscriptLine.Plain(string.Empty)];
        if (TryRule(line, out var rule))
            return [new TranscriptRunBuilder().Append(TuiText.Pad("  " + rule, width), dim: true).Line()];
        if (TryQuote(line, out var quote))
            return [new TranscriptRunBuilder().Append(Gutter, dim: true).Append(PresentationInline(quote)).Line()];
        if (TryHeading(line, out var heading, out var level))
        {
            // Preserve the existing heading/inline reset boundaries without sending
            // those boundaries through ANSI. A styled inline closes the outer paint.
            var activeHeading = true;
            var runs = new List<TranscriptRun>();
            foreach (var run in PresentationInline(heading))
            {
                runs.Add(run with { Token = activeHeading && run.Token == TuiToken.Ink ? TuiToken.Warn : run.Token,
                    Bold = run.Bold || (activeHeading && level == 1) });
                if (run.Token != TuiToken.Ink || run.Bold || run.Dim) activeHeading = false;
            }
            return [new(runs)];
        }
        if (TryTable(line, out var table)) return [new(PresentationInline(table))];
        if (TryNestedBullet(line, out var nested))
            return [new TranscriptRunBuilder().Append("    ").Append("•", TuiToken.Brand2).Append("  ").Append(PresentationInline(nested)).Line()];
        if (IsDeeperBullet(line)) return [new(PresentationInline(line.TrimEnd()))];
        if (TryBullet(line, out var bullet))
            return [new TranscriptRunBuilder().Append("  ").Append("•", TuiToken.Brand2).Append("  ").Append(PresentationInline(bullet)).Line()];
        if (TryOrdered(line, out var marker, out var ordered))
            return [new TranscriptRunBuilder().Append("  ").Append(marker, TuiToken.Brand2).Append("  ").Append(PresentationInline(ordered)).Line()];
        return [new(PresentationInline(line))];
    }

    /// <summary>
    /// Inline runs for one fragment. Parsing goes through Markdig (CommonMark), so nesting,
    /// escapes and delimiters that arrive only half-complete degrade to literal text instead
    /// of being paired by a scanner.
    /// </summary>
    internal static IReadOnlyList<TranscriptRun> PresentationInline(string text, bool continuesWord = false)
    {
        // Kept for call-site compatibility: the old scanner needed the flag to tell an
        // emphasis closer from an opener. A real parser resolves that from the delimiters.
        _ = continuesWord;
        return MarkdigInline.Parse(text);
    }
}
