namespace RuAgent.Cli.Tui;

/// <summary>
/// Places a bordered interaction panel (approval, picker, secret input) in the middle of the
/// full-screen frame. The panel keeps its own compact width; the rows receive a symmetric
/// indent so the dialog reads as floating over the footer band instead of hugging its left
/// edge. Pure arithmetic over already-composed rows, so snapshot tests can pin the placement
/// at every supported viewport width.
/// </summary>
public static class TuiFloatingPanel
{
    /// <summary>Widest floating panel; wider terminals keep the dialog compact.</summary>
    public const int MaxWidth = 72;

    /// <summary>Narrowest floating panel that still reads as a picker frame.</summary>
    public const int MinWidth = SlashDropdown.MinWidth;

    /// <summary>Panel width for a viewport, leaving a margin on both sides.</summary>
    public static int ComputeWidth(int screenWidth) =>
        Math.Clamp(screenWidth - 8, MinWidth, MaxWidth);

    /// <summary>
    /// Centers <paramref name="lines"/> horizontally: every row is prefixed with the same
    /// number of spaces. Rows are never padded on the right; the footer erase handles the
    /// remainder of the screen. Returns the input unchanged when there is no room to center.
    /// </summary>
    public static IReadOnlyList<string> Center(IReadOnlyList<string> lines, int screenWidth)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (screenWidth <= 0 || lines.Count == 0)
        {
            return lines;
        }

        var widest = 0;
        foreach (var line in lines)
        {
            widest = Math.Max(widest, TuiText.VisibleWidth(line));
        }

        var left = Math.Max(0, (screenWidth - widest) / 2);
        if (left == 0)
        {
            return lines;
        }

        var indent = new string(' ', left);
        var centered = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            centered.Add(indent + line);
        }

        return centered;
    }
}
