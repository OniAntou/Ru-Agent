using System.Globalization;
using System.Text;

namespace RuApp.Controls;

/// <summary>
/// The chat code card's decisions, kept apart from WinUI so they are testable:
/// when a block folds, how much it shows folded, the line-number gutter and the
/// footer wording. Copy always takes the whole source, never this view of it.
/// </summary>
public static class CodeCardLayout
{
    /// <summary>A block longer than this folds once its fence has closed.</summary>
    public const int FoldAboveLines = 30;

    /// <summary>How many lines a folded block keeps on screen.</summary>
    public const int FoldedLines = 20;

    /// <summary>
    /// Folds only a finished block: a fence still streaming keeps growing in place,
    /// so its reader never watches the tail vanish under a footer.
    /// </summary>
    public static bool Folds(int lineCount, bool complete) => complete && lineCount > FoldAboveLines;

    /// <summary>
    /// The gutter is one text column beside one selectable code column. Wrapped lines
    /// grow taller than a gutter row, so numbers show only while wrap is off.
    /// </summary>
    public static bool ShowsLineNumbers(bool requested, bool wrap) => requested && !wrap;

    public static string Gutter(int lineCount)
    {
        var numbers = new StringBuilder();
        for (int index = 1; index <= lineCount; index++)
        {
            if (index > 1) numbers.Append('\n');
            numbers.Append(index.ToString(CultureInfo.InvariantCulture));
        }

        return numbers.ToString();
    }

    public static string FoldLabel(int lineCount, bool expanded) => expanded
        ? "Show fewer lines"
        : $"Show all {lineCount.ToString("N0", CultureInfo.InvariantCulture)} lines";
}
