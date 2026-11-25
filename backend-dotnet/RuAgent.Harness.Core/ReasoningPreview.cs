using System;
using System.Text;

namespace RuAgent.Harness.Core;

/// <summary>
/// The one-line summary a running thinking row shows: the first line of the newest
/// paragraph whose first line is already complete, matching the DeepSeek Harness rule.
/// </summary>
/// <remarks>
/// A paragraph's first line counts as complete when a newline follows it — either another
/// line of the same paragraph or the blank line that ends the paragraph. An unfinished
/// single-line paragraph therefore shows nothing, and a long paragraph stops advancing at
/// its own first line instead of flickering with every token.
/// </remarks>
public static class ReasoningPreview
{
    /// <summary>Longest summary line a frame may carry, in UTF-16 characters.</summary>
    public const int MaximumCharacters = 400;

    /// <summary>The tail of the reasoning kept for the paragraph scan, in UTF-16 characters.</summary>
    public const int MaximumTrackedCharacters = 8_192;

    /// <summary>
    /// The first line of the newest completed paragraph, cleaned for display.
    /// </summary>
    /// <param name="text">Accumulated (already redacted) reasoning for one model step.</param>
    /// <returns>One display line, or an empty string when no paragraph has completed a first line.</returns>
    public static string Summary(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var normalized = Normalize(text);
        var latest = string.Empty;
        var paragraphStart = 0;
        while (paragraphStart <= normalized.Length)
        {
            var separator = FindParagraphSeparator(normalized, paragraphStart);
            var paragraphEnd = separator.Start >= 0 ? separator.Start : normalized.Length;
            var newline = normalized.IndexOf('\n', paragraphStart);
            if (newline >= 0 && newline <= paragraphEnd)
            {
                var line = normalized[paragraphStart..newline].Trim();
                if (line.Length > 0)
                {
                    latest = line;
                }
            }

            if (separator.Start < 0)
            {
                break;
            }

            paragraphStart = separator.End;
        }

        return Clean(latest);
    }

    /// <summary>
    /// Prepares one summary line for display: no emphasis markers, no control characters,
    /// bounded length with an ellipsis that never splits a surrogate pair.
    /// </summary>
    /// <param name="line">Raw first line of a completed paragraph.</param>
    /// <returns>The display line.</returns>
    public static string Clean(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var stripped = line.Replace("**", string.Empty, StringComparison.Ordinal);
        var builder = new StringBuilder(stripped.Length);
        foreach (var character in stripped)
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        var trimmed = builder.ToString().Trim();
        if (trimmed.Length <= MaximumCharacters)
        {
            return trimmed;
        }

        var cut = MaximumCharacters - 1;
        if (char.IsHighSurrogate(trimmed[cut - 1]))
        {
            cut--;
        }

        return trimmed[..cut].TrimEnd() + "…";
    }

    private static string Normalize(string text) =>
        text.Contains('\r')
            ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            : text;

    /// <summary>
    /// Finds the next paragraph boundary: a newline followed by one or more blank lines
    /// (a line of spaces or tabs only). Returns the boundary's first and one-past-last
    /// newline indexes, or (-1, -1) when the text has no further boundary.
    /// </summary>
    private static (int Start, int End) FindParagraphSeparator(string text, int from)
    {
        for (var index = from; index < text.Length; index++)
        {
            if (text[index] != '\n')
            {
                continue;
            }

            var end = index + 1;
            while (end < text.Length)
            {
                var next = end;
                while (next < text.Length && text[next] is ' ' or '\t')
                {
                    next++;
                }

                if (next >= text.Length || text[next] != '\n')
                {
                    break;
                }

                end = next + 1;
            }

            if (end > index + 1)
            {
                return (index, end);
            }
        }

        return (-1, -1);
    }
}
