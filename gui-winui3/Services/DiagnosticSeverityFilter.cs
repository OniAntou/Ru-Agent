namespace RuApp.Services;

/// <summary>
/// The diagnostics severity filter: the levels a log record can carry, the order they
/// are offered in, and how a selected index maps to a level.
///
/// The three names were written in three places — the combo box items in
/// <c>DiagnosticsView.xaml</c> and an identical index switch copied into both
/// <c>ApplyFilter</c> and the export handler — so the list shown to the user and the
/// list filtered on could drift apart silently, and a fourth level would have had to
/// be added in three files.
/// </summary>
public static class DiagnosticSeverityFilter
{
    /// <summary>Lowest severity first, which is the order the picker offers them.</summary>
    public static IReadOnlyList<string> Levels { get; } = ["info", "warning", "error"];

    /// <summary>The picker's first entry: no level filter.</summary>
    public static string AnyLevelLabel => "All Levels";

    /// <summary>
    /// Everything the picker offers, in order, so the combo box has one source and its
    /// indices line up with <see cref="ForSelectedIndex"/> by construction.
    /// </summary>
    public static IReadOnlyList<string> PickerLabels { get; } =
        [AnyLevelLabel, .. Levels];

    /// <summary>
    /// The picker's first entry means "any level"; the rest line up with
    /// <see cref="Levels"/> one for one.
    /// </summary>
    public const int FirstLevelIndex = 1;

    /// <summary>
    /// The level matching a picker index, or null for "any level". Out-of-range
    /// indices mean "any level" rather than throwing: the picker can report -1 before
    /// it has been populated.
    /// </summary>
    public static string? ForSelectedIndex(int selectedIndex)
    {
        int offset = selectedIndex - FirstLevelIndex;
        return offset >= 0 && offset < Levels.Count ? Levels[offset] : null;
    }

    /// <summary>
    /// Index that selects a level, or 0 for "any level" when the name is unknown, so a
    /// record with an unexpected level does not silently filter everything out.
    /// </summary>
    public static int IndexOf(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            return 0;
        }

        for (var index = 0; index < Levels.Count; index++)
        {
            if (string.Equals(Levels[index], level.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return index + FirstLevelIndex;
            }
        }

        return 0;
    }
}
