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

    private static TuiToken DiffToken(string line) =>
        line.StartsWith("+", StringComparison.Ordinal) ? TuiToken.Ok
        : line.StartsWith("-", StringComparison.Ordinal) ? TuiToken.Err
        : TuiToken.Ink;

    private static string ContentRow(string content, TuiToken token) =>
        Border("│") + " " + TuiPalette.Paint(content, token) + " " + Border("│");

    private static string Border(string glyph) => TuiPalette.Paint(glyph, TuiToken.Line);
}
