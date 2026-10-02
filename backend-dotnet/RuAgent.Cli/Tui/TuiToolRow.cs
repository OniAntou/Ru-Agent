namespace RuAgent.Cli.Tui;

/// <summary>
/// The inline tool row cloned from OpenCode: a one-cell icon followed by the tool display
/// name, its target and bounded metrics. Completed rows are muted; failures flip to the
/// error tone. Pure string-in/line-out so snapshot tests can pin the output.
/// </summary>
public static class TuiToolRow
{
    /// <summary>Narrowest row this renderer produces; requested widths below this clamp up.</summary>
    public const int MinWidth = 14;

    /// <summary>
    /// One settled tool row: <c>{icon} {Display} {target} {meta}</c>, truncated to
    /// <paramref name="width"/> cells. Meta is already composed (e.g. <c>(18 matches)</c>)
    /// by the transcript; a blank value leaves it off.
    /// </summary>
    public static string Compose(
        ToolLineStatus status, string display, string? target, string? meta, int width)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(display);
        var w = Math.Max(MinWidth, width);
        var token = status == ToolLineStatus.Failed ? TuiToken.Err : TuiToken.Meta;

        var text = display;
        if (!string.IsNullOrWhiteSpace(target))
        {
            text += " " + target;
        }

        if (!string.IsNullOrWhiteSpace(meta))
        {
            text += " " + meta;
        }

        return TuiText.Truncate(
            TuiPalette.Paint(Icon(display), token) + " " + TuiPalette.Paint(text, token),
            w);
    }

    /// <summary>Icon for a provider tool display name (OpenCode's glyph vocabulary).</summary>
    public static string Icon(string display) => display switch
    {
        "View" or "Read" or "Docs" => "→",
        "Explore" or "Search" => "✱",
        "Edit" or "Write" or "Delete" => "←",
        "Terminal" => "$",
        "Fetch" => "%",
        _ => "⚙",
    };
}
