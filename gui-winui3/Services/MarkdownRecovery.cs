using System.Text.RegularExpressions;

namespace RuApp.Services;

/// <summary>
/// Recovers paragraph breaks collapsed before they reached the transcript.
/// Old sessions persisted with every newline folded to a space, so a whole reply
/// arrives as one overlong line with its section headings still inside it.
/// Only lines past the length bound are touched: normal chat lines, code fences
/// and short prose pass through exactly as they arrived.
/// </summary>
internal static partial class MarkdownRecovery
{
    /// <summary>
    /// Lines longer than this may hold collapsed headings and are split.
    /// </summary>
    internal const int MaxIntactLineLength = 400;

    /// <summary>
    /// Splits an overlong line before inline numbered headings ("## 1. ...").
    /// The trailing dot keeps prose like "C# 8" intact; the worst case for a
    /// false positive is one extra line break in an already unreadable wall.
    /// </summary>
    public static IReadOnlyList<string> SplitLine(string line)
    {
        if (string.IsNullOrEmpty(line) || line.Length <= MaxIntactLineLength)
        {
            return new[] { line };
        }

        var pieces = InlineHeading().Split(line);
        if (pieces.Length <= 1)
        {
            return new[] { line };
        }

        var recovered = new List<string>(pieces.Length);
        foreach (var piece in pieces)
        {
            var trimmed = piece.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                recovered.Add(trimmed);
            }
        }

        return recovered.Count > 0 ? recovered : new[] { line };
    }

    [GeneratedRegex(@"(?<=\s)(?=#{1,3} \d+\.)", RegexOptions.CultureInvariant)]
    private static partial Regex InlineHeading();
}
