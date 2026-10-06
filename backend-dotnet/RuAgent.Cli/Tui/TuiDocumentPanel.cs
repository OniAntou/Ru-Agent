namespace RuAgent.Cli.Tui;

/// <summary>
/// Scroll arithmetic for the read-only output card. The card itself is composed by
/// <see cref="TuiCard"/> like every other floating surface, so this only owns the two rules a
/// viewer needs: an offset that stays inside the rows, and the last offset that still shows
/// the final row. Pure arithmetic, so snapshot tests pin it without a terminal.
/// </summary>
public static class TuiDocumentPanel
{
    public const int MinWidth = 32;

    /// <summary>Largest offset that still shows the last row, so the tail never scrolls off.</summary>
    public static int MaxOffset(int totalRows, int pageSize) => Math.Max(0, totalRows - pageSize);

    public static int ClampOffset(int offset, int totalRows, int pageSize) =>
        Math.Clamp(offset, 0, MaxOffset(totalRows, pageSize));
}