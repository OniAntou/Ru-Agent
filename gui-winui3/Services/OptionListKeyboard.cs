using System;
using Windows.System;

namespace RuApp.Services;

/// <summary>
/// Arrow-key navigation over the option rows of a flyout.
/// </summary>
/// <remarks>
/// Both option flyouts in the app need this and neither can have hundreds of rows as tab
/// stops: the model list is every model the provider publishes, which on models.dev is
/// several hundred, so a user pressing Tab once lands in a list they then have to Tab
/// through the rest of. The rows are reachable by arrow key and the search field, or the
/// list container, keeps the focus, which is what a native combo box does.
///
/// The controller counts rows and remembers where the cursor is. It holds no elements and
/// draws nothing, for two reasons. A WinUI control cannot be constructed outside a
/// running XAML application, so a version of this that took a list of buttons could not be
/// tested at all, and the whole point of sharing it is that the key table cannot drift
/// between the two flyouts. And the view already owns the rows: it paints the cursor, it
/// decides what a selection means, and it is the only thing that knows how to scroll its
/// own list.
///
/// One controller per rebuild. A flyout rebuilds its rows whenever the catalog publishes,
/// and a controller that outlived its rows would be one more piece of state to keep in
/// step with a list that no longer exists.
/// </remarks>
public sealed class OptionListKeyboard
{
    /// <summary>How far PageUp and PageDown move.</summary>
    public const int PageStep = 8;

    private readonly int _count;
    private readonly Action<int> _activate;
    private readonly Action _dismiss;
    private readonly Action<int>? _ensureVisible;
    private int _cursor = -1;

    /// <summary>
    /// Creates a controller over a list of <paramref name="count"/> rows.
    /// </summary>
    /// <param name="count">How many rows the list has. Fixed for this controller's life.</param>
    /// <param name="activate">Called with the row index to commit.</param>
    /// <param name="dismiss">Called when the user asks to leave without choosing.</param>
    /// <param name="ensureVisible">
    /// Brings a row on screen, or null when the caller has no scrolling to do.
    /// </param>
    public OptionListKeyboard(
        int count,
        Action<int> activate,
        Action dismiss,
        Action<int>? ensureVisible = null)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _count = count;
        _activate = activate ?? throw new ArgumentNullException(nameof(activate));
        _dismiss = dismiss ?? throw new ArgumentNullException(nameof(dismiss));
        _ensureVisible = ensureVisible;
    }

    /// <summary>Raised when the cursor moves, carrying its new index.</summary>
    public event Action<int>? CursorChanged;

    /// <summary>Where the cursor is, or -1 when no row is marked.</summary>
    public int Cursor => _cursor;

    /// <summary>
    /// Moves the cursor to an index, clamped to the list, and optionally brings the row
    /// into view.
    /// </summary>
    /// <param name="index">The wanted index; -1 clears the cursor.</param>
    /// <param name="show">
    /// False when the caller already knows the row is on screen. Asking again is a
    /// scroll request per pointer move across the list, and the pointer never needs one
    /// because it just arrived there.
    /// </param>
    /// <returns>True when the cursor actually moved.</returns>
    public bool MoveCursor(int index, bool show = true)
    {
        var wanted = _count == 0 ? -1 : Math.Clamp(index, -1, _count - 1);
        if (wanted == _cursor)
        {
            return false;
        }

        _cursor = wanted;
        if (wanted >= 0 && show)
        {
            _ensureVisible?.Invoke(wanted);
        }

        CursorChanged?.Invoke(_cursor);
        return true;
    }

    /// <summary>
    /// Moves the cursor by a number of rows, stopping at the ends.
    /// </summary>
    /// <param name="delta">Negative moves towards the first row.</param>
    /// <remarks>
    /// Clamps to the list rather than to "nowhere". Arrowing up off the first row used to
    /// clear the mark, and a highlight that disappears as the user keeps pressing Up
    /// reads as a broken control rather than as the end of a list. The clamp also covers
    /// the other end for free: from no cursor at all, any direction lands on the first
    /// row, because -1 plus one is zero. Counting from "no row" the other way would skip
    /// the first row, which is the row most likely to be the one wanted.
    ///
    /// <see cref="MoveCursor"/> keeps a wider clamp on purpose, so that -1 stays reachable
    /// as an explicit clear.
    /// </remarks>
    public void MoveCursorBy(int delta) =>
        MoveCursor(_count == 0 ? -1 : Math.Clamp(_cursor + delta, 0, _count - 1));

    /// <summary>
    /// Moves the cursor to the first or the last row.
    /// </summary>
    /// <param name="last">True for the last row, false for the first.</param>
    public void MoveCursorToEdge(bool last) => MoveCursor(last ? _count - 1 : 0);

    /// <summary>Commits the row under the cursor, if there is one.</summary>
    public void ActivateCursor()
    {
        if (_cursor >= 0)
        {
            _activate(_cursor);
        }
    }

    /// <summary>
    /// Handles one key. Returns true when the key belonged to the list, which the caller
    /// uses to decide it should swallow the event.
    /// </summary>
    /// <param name="key">The key that was pressed.</param>
    /// <param name="spaceActivates">
    /// Whether Space should commit. False when a text field holds the focus, because
    /// Space there is a character and the user is typing a search, not choosing.
    /// </param>
    public bool HandleKey(VirtualKey key, bool spaceActivates)
    {
        // Escape is answered before the list is looked at. A user whose query matched
        // nothing is looking at an empty panel with no row to point at, and Escape has to
        // work there: gating it on there being rows would mean the emptier the results,
        // the harder the list is to leave.
        if (key == VirtualKey.Escape)
        {
            _dismiss();
            return true;
        }

        if (_count == 0)
        {
            return false;
        }

        switch (key)
        {
            case VirtualKey.Up:
                MoveCursorBy(-1);
                break;
            case VirtualKey.Down:
                MoveCursorBy(1);
                break;
            case VirtualKey.PageUp:
                MoveCursorBy(-PageStep);
                break;
            case VirtualKey.PageDown:
                MoveCursorBy(PageStep);
                break;
            case VirtualKey.Home:
                MoveCursorToEdge(last: false);
                break;
            case VirtualKey.End:
                MoveCursorToEdge(last: true);
                break;
            case VirtualKey.Enter:
                ActivateCursor();
                break;
            case VirtualKey.Space when spaceActivates:
                ActivateCursor();
                break;
            default:
                return false;
        }

        return true;
    }
}
