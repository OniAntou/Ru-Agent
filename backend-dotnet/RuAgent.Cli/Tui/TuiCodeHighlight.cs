namespace RuAgent.Cli.Tui;

/// <summary>
/// ANSI-only tinting for fenced code lines: a right-aligned line-number gutter in the
/// existing <c>│ </c> style followed by keyword/string/comment/number spans painted
/// through <see cref="TuiPalette"/>. Stateless and per-line: a block comment must close
/// on the same line (an unterminated one tints to the end of the line) and no language
/// grammar survives between calls, so the transcript can render streamed lines one at a
/// time. The generic keyword set is deliberately language-agnostic; no regex or grammar
/// library is involved.
/// </summary>
public static class TuiCodeHighlight
{
    private static readonly HashSet<string> Keywords = new(
        "abstract and as async await base bool break byte case catch char class const continue decimal def default defer del do double elif else enum event except explicit export extends extern False false final finally fixed float for foreach from function get global goto if implements implicit import in int interface internal is lambda let lock long namespace new None none nonlocal not null object operator or out override params partial pass private protected public raise readonly ref return sbyte sealed set short sizeof stackalloc static string struct switch this throw True true trap try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while with yield"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);

    /// <summary>
    /// Renders one code line for <paramref name="width"/> cells as a single row: the
    /// <c>{number,3} │ </c> gutter then the tinted body, clipped with an ellipsis when the
    /// line is too long. Colour degrades with <paramref name="level"/>; the palette level
    /// is restored before returning and <see cref="TuiColorLevel.None"/> emits no escapes.
    /// </summary>
    public static IReadOnlyList<string> RenderLine(string line, int lineNumber, int width, TuiColorLevel level)
    {
        ArgumentNullException.ThrowIfNull(line);
        using var scope = TuiPalette.PinLevel(level);

        var gutter = $"{lineNumber,3} │ ";
        var body = TuiText.Truncate(Highlight(line), Math.Max(10, width - gutter.Length));
        return [TuiPalette.DimPaint(gutter) + body];
    }

    private static string Highlight(string line)
    {
        var output = new System.Text.StringBuilder(line.Length + 32);
        var index = 0;
        while (index < line.Length)
        {
            if (line[index] == '/' && index + 1 < line.Length && line[index + 1] == '/')
            {
                output.Append(TuiPalette.Paint(line[index..], TuiToken.Meta));
                break;
            }

            if (line[index] == '/' && index + 1 < line.Length && line[index + 1] == '*')
            {
                var close = line.IndexOf("*/", index + 2, StringComparison.Ordinal);
                var stop = close < 0 ? line.Length : close + 2;
                output.Append(TuiPalette.Paint(line[index..stop], TuiToken.Meta));
                index = stop;
                continue;
            }

            var c = line[index];
            if (c == '"' || c == '\'')
            {
                var stop = index + 1;
                while (stop < line.Length)
                {
                    if (line[stop] == '\\' && stop + 1 < line.Length)
                    {
                        stop += 2;
                        continue;
                    }

                    stop++;
                    if (line[stop - 1] == c)
                    {
                        break;
                    }
                }

                output.Append(TuiPalette.Paint(line[index..stop], TuiToken.Ok));
                index = stop;
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                var stop = index + 1;
                while (stop < line.Length && (char.IsAsciiDigit(line[stop]) || line[stop] is '.' or '_'))
                {
                    stop++;
                }

                output.Append(TuiPalette.Paint(line[index..stop], TuiToken.Brand));
                index = stop;
                continue;
            }

            if (IsWordChar(c))
            {
                var stop = index + 1;
                while (stop < line.Length && IsWordChar(line[stop]))
                {
                    stop++;
                }

                var word = line[index..stop];
                output.Append(Keywords.Contains(word) ? TuiPalette.Paint(word, TuiToken.Brand2) : word);
                index = stop;
                continue;
            }

            output.Append(c);
            index++;
        }

        return output.ToString();
    }

    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '$' or '@';
}
