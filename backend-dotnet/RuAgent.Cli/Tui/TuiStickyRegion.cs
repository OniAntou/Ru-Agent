using System.Text;

namespace RuAgent.Cli.Tui;

/// <summary>
/// Reserves the bottom rows of the viewport for the live row and the composer with DECSTBM,
/// so agent output scrolls above a footer that never moves. The region is only ever entered on
/// a live console; redirected output and <c>--classic</c> keep the sequential layout.
///
/// Every layout change is a single reset-then-rebuild batch: a scrolling region left over from
/// a previous window size is the thing that shreds the screen when a terminal is resized, so
/// <see cref="Exit"/> and <see cref="Reapply"/> always reset it before doing anything else.
/// All writes go through <see cref="TuiSurface"/> so footer repaints cannot interleave with
/// transcript appends.
/// </summary>
public sealed class TuiStickyRegion
{
    private readonly TuiSurface _surface;

    public TuiStickyRegion(TuiSurface surface)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
    }

    public bool Active { get; private set; }

    /// <summary>Rows the region was built for.</summary>
    public int Rows { get; private set; }

    public int FooterHeight { get; private set; }

    /// <summary>First row owned by the footer, or zero when inactive.</summary>
    public int FooterFirst { get; private set; }

    /// <summary>
    /// Transcript rows are <c>1..Bottom</c>; the footer occupies the rows below. Returns
    /// <c>(0, 0)</c> when the viewport is too short to keep a transcript row.
    /// </summary>
    public static (int Top, int Bottom) Compute(int rows, int footerHeight)
    {
        if (rows <= 0 || footerHeight <= 0 || rows - footerHeight < 2)
        {
            return (0, 0);
        }

        return (1, rows - footerHeight);
    }

    /// <summary>Rows the footer can use on this viewport, or zero when sticky mode must not run.</summary>
    public static int ResolveFooterHeight(int rows, int requested)
    {
        var (_, bottom) = Compute(rows, requested);
        return bottom == 0 ? 0 : requested;
    }

    /// <summary>Enters the region for a fresh layout. Does not clear the screen.</summary>
    public bool Enter(int rows, int footerHeight)
    {
        var (_, bottom) = Compute(rows, footerHeight);
        if (bottom == 0)
        {
            TuiDebug.Log("sticky", $"enter rejected rows={rows} footer={footerHeight}");
            return false;
        }

        Rows = rows;
        FooterHeight = footerHeight;
        FooterFirst = bottom + 1;
        Active = true;
        Write($"\u001b[1;{bottom}r");
        TuiDebug.Log("sticky", $"enter rows={rows} footer={footerHeight} first={FooterFirst} bottom={bottom}");
        return true;
    }

    /// <summary>
    /// Rebuilds the region for a new viewport inside one batch: the old scrolling region is
    /// reset, whatever the old footer painted is erased, and the new region is installed.
    /// Never addresses a row below the live viewport.
    /// </summary>
    public bool Reapply(int rows, int footerHeight)
    {
        var (_, bottom) = Compute(rows, footerHeight);
        if (bottom == 0)
        {
            Exit();
            return false;
        }

        if (!Active)
        {
            return Enter(rows, footerHeight);
        }

        var previousFirst = FooterFirst;
        Rows = rows;
        FooterHeight = footerHeight;
        FooterFirst = bottom + 1;
        Active = true;

        var builder = new StringBuilder();
        builder.Append("\u001b[r");
        AppendClearDown(builder, Math.Min(previousFirst, FooterFirst));
        builder.Append($"\u001b[1;{bottom}r");
        Write(builder.ToString());
        TuiDebug.Log("sticky", $"reapply rows={rows} footer={footerHeight} first={FooterFirst} prevFirst={previousFirst}");
        return true;
    }

    /// <summary>
    /// Leaves sticky mode: resets the scrolling region, erases the footer, and parks the
    /// cursor where the next transcript line belongs. Safe to call when already inactive.
    /// </summary>
    public void Exit()
    {
        if (!Active)
        {
            return;
        }

        var first = FooterFirst;
        Active = false;
        Rows = 0;
        FooterHeight = 0;
        FooterFirst = 0;

        var builder = new StringBuilder();
        builder.Append("\u001b[r");
        AppendClearDown(builder, first);
        builder.Append($"\u001b[{Math.Max(1, first - 1)};1H");
        Write(builder.ToString());
        TuiDebug.Log("sticky", $"exit first={first}");
    }

    /// <summary>
    /// True when the region was built for a different terminal height than the live one. While
    /// that holds, nothing may paint: the window has already moved underneath the layout, and a
    /// window that grew leaves the old bottom rows in the middle of the transcript.
    /// </summary>
    public static bool LayoutIsStale(int regionRows, bool hasLiveSize, int liveRows) =>
        hasLiveSize && liveRows != regionRows;

    /// <summary>
    /// Draws every footer row at its fixed position and restores the transcript cursor. When
    /// <paramref name="caretRowInFooter"/> is given (1-based within the footer) the hardware
    /// cursor is parked inside the composer instead, so IME popups follow the text.
    ///
    /// Nothing is painted while the layout is stale — that is, while the live terminal size no
    /// longer matches the geometry this region was built for. A window that grew keeps its
    /// content in place, so painting the old bottom rows would strand a live row and a border
    /// in the middle of the transcript; the resize watcher rebuilds the region shortly after.
    /// </summary>
    public void Paint(IReadOnlyList<string> footerLines, int width, int? caretRowInFooter = null, int caretColumn = 1)
    {
        ArgumentNullException.ThrowIfNull(footerLines);
        if (!Active || FooterHeight <= 0)
        {
            return;
        }

        if (LayoutIsStale(Rows, TuiViewport.TryReadLiveSize(out _, out var liveRows), liveRows))
        {
            TuiDebug.Log("sticky", $"paint skipped stale rows={Rows} live={liveRows}");
            return;
        }

        var lastRow = Rows;
        var builder = new StringBuilder();
        var painted = 0;

        for (var i = 0; i < FooterHeight; i++)
        {
            var row = FooterFirst + i;
            if (row > lastRow)
            {
                break;
            }

            builder.Append($"\u001b[{row};1H");
            builder.Append("\u001b[2K");
            if (i < footerLines.Count)
            {
                builder.Append(TuiText.Truncate(footerLines[i], width));
            }

            painted++;
        }

        if (caretRowInFooter is { } caretRow && painted > 0)
        {
            var row = Math.Min(FooterFirst + Math.Max(0, caretRow - 1), lastRow);
            builder.Append($"\u001b[{row};{Math.Max(1, caretColumn)}H");
        }
        else if (painted > 0)
        {
            // Park on the last transcript row so the next append scrolls normally.
            builder.Append($"\u001b[{Math.Max(1, FooterFirst - 1)};1H");
        }

        if (builder.Length > 0)
        {
            Write(builder.ToString());
        }
    }

    /// <summary>
    /// Escape sequence that erases the footer and everything below it, starting from the row
    /// the cursor should end up on. Pure so the geometry can be tested without a console.
    /// Returns nothing when there is no footer row to clear — in particular when the footer
    /// sits below a shrunken viewport, where erasing would eat a line of transcript.
    /// </summary>
    public static string ComposeClearDown(int firstFooterRow, int liveRows)
    {
        if (firstFooterRow <= 1 || liveRows < 1)
        {
            return string.Empty;
        }

        var row = firstFooterRow - 1;
        if (row >= liveRows)
        {
            return string.Empty;
        }

        return $"\u001b[{row};1H\u001b[J";
    }

    /// <summary>Erases the footer area. Nothing is emitted when there is no live screen.</summary>
    private static void AppendClearDown(StringBuilder builder, int firstFooterRow)
    {
        if (!TuiViewport.TryReadLiveSize(out _, out var rows))
        {
            return;
        }

        builder.Append(ComposeClearDown(firstFooterRow, rows));
    }

    private void Write(string text) => _surface.Repaint(writer => writer.Write(text));
}
