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

    internal static IReadOnlyList<TranscriptRun> PresentationInline(string text, bool continuesWord = false)
    {
        var output = new TranscriptRunBuilder();
        var index = 0;
        var previous = continuesWord ? 'x' : ' ';
        while (index < text.Length)
        {
            var c = text[index];
            if (c == '\\' && index + 1 < text.Length && IsEscapable(text[index + 1]))
            { output.Append(text[index + 1]); previous = text[index + 1]; index += 2; continue; }
            if (c == '`')
            {
                var close = text.IndexOf('`', index + 1);
                if (close < 0) { output.Append(text[index..]); break; }
                output.Append(text[(index + 1)..close], TuiToken.Ok);
                previous = '`'; index = close + 1; continue;
            }
            if ((c == '*' || c == '_') && index + 1 < text.Length && text[index + 1] == c)
            {
                if (IsWordChar(previous)) { output.Append(c); previous = c; index++; continue; }
                var close = FindClose(text, index + 2, c);
                if (close < 0) { output.Append(text[index..]); break; }
                output.Append(text[(index + 2)..close], TuiToken.Bright, bold: true);
                previous = c; index = close + 2; continue;
            }
            if (c == '~' && index + 1 < text.Length && text[index + 1] == '~')
            {
                var close = text.IndexOf("~~", index + 2, StringComparison.Ordinal);
                if (close < 0) { output.Append(text[index..]); break; }
                output.Append(text[(index + 2)..close], dim: true);
                previous = '~'; index = close + 2; continue;
            }
            if (c == '[')
            {
                var label = FindLinkLabel(text, index, out var after);
                if (label is not null)
                { output.Append(PresentationInline(label)); previous = ']'; index = after; continue; }
            }
            output.Append(c);
            previous = char.IsWhiteSpace(c) ? ' ' : c;
            index++;
        }
        return output.Line().Runs;
    }
}
