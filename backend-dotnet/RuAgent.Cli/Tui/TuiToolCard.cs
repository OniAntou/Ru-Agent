namespace RuAgent.Cli.Tui;

/// <summary>
/// The rounded tool card: one header row carrying the caller-composed title (past-tense verb
/// plus target plus elapsed, see <see cref="TuiToolVerbs"/>) above diff body rows coloured
/// green/red by their leading <c>+</c>/<c>-</c> marker. Pure string-in/rows-out; every row is
/// exactly the card width, so snapshot tests can pin the output.
/// </summary>
public static class TuiToolCard
{
    /// <summary>Narrowest card this renderer produces; requested widths below this clamp up.</summary>
    public const int MinWidth = 14;

    /// <summary>
    /// Renders the card for <paramref name="width"/> cells: rounded border, title row, one row
    /// per diff line (truncated to the inner width, never wrapped) and a closing border.
    /// Colour degrades with <paramref name="level"/> through <see cref="TuiPalette"/>; at
    /// <see cref="TuiColorLevel.None"/> every row is plain text. The palette level is restored
    /// before returning.
    /// </summary>
    public static IReadOnlyList<string> Render(
        string title, IReadOnlyList<string> lines, int width, TuiColorLevel level)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(lines);

        var inner = Math.Max(MinWidth, width) - 4;
        using var scope = TuiPalette.PinLevel(level);

        var rows = new List<string>(lines.Count + 3)
        {
            Border("╭" + new string('─', inner + 2) + "╮"),
            ContentRow(TuiText.Pad(title, inner), TuiToken.Brand),
        };

        foreach (var line in lines)
        {
            rows.Add(ContentRow(TuiText.Pad(line, inner), DiffToken(line)));
        }

        rows.Add(Border("╰" + new string('─', inner + 2) + "╯"));
        return rows;
    }

    /// <summary>
    /// The collapsed card: one framed summary row with no body. <paramref name="expandable"/>
    /// appends the <c>ctrl+o</c> affordance when diff hunks were retained behind the card.
    /// Exactly <paramref name="width"/> cells, so a card never wraps the transcript frame.
    /// </summary>
    public static string RenderSummary(string summary, int width, bool expandable, TuiColorLevel level)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var w = Math.Max(MinWidth, width);
        using var scope = TuiPalette.PinLevel(level);

        var text = summary.Trim();
        if (expandable)
        {
            text += " · ctrl+o";
        }

        var budget = w - 5;
        if (TuiText.VisibleWidth(text) > budget)
        {
            text = TuiText.Truncate(text, budget);
        }

        var fill = Math.Max(0, w - 5 - TuiText.VisibleWidth(text));
        var token = summary.StartsWith("✓", StringComparison.Ordinal)
            ? TuiToken.Ok
            : summary.StartsWith("✗", StringComparison.Ordinal)
                ? TuiToken.Err
                : TuiToken.Brand;
        return Border("╭─")
            + " "
            + TuiPalette.Paint(text, token)
            + " "
            + Border(new string('─', fill))
            + Border("╮");
    }

    /// <summary>
    /// The footer's live tool row: the in-flight tool activity on one rail line, repainted in
    /// place as the activity changes. A null or blank activity leaves the row empty but keeps
    /// its width, so the fixed three-row footer never shifts.
    /// </summary>
    public static string RenderLiveRow(string? activity, int width, TuiColorLevel level)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        using var scope = TuiPalette.PinLevel(level);
        if (string.IsNullOrWhiteSpace(activity))
        {
            return new string(' ', width);
        }

        var head = Border("  ▌ ") + TuiPalette.Paint(TuiText.Truncate(activity.Trim(), Math.Max(1, width - 4)), TuiToken.Brand2);
        return TuiText.Pad(head, width);
    }

    private static TuiToken DiffToken(string line) =>
        line.StartsWith("+", StringComparison.Ordinal) ? TuiToken.Ok
        : line.StartsWith("-", StringComparison.Ordinal) ? TuiToken.Err
        : TuiToken.Ink;

    private static string ContentRow(string content, TuiToken token) =>
        Border("│") + " " + TuiPalette.Paint(content, token) + " " + Border("│");

    private static string Border(string glyph) => TuiPalette.Paint(glyph, TuiToken.Line);
}
