namespace RuAgent.Cli.Tui;

/// <summary>
/// Turns a stream of observed terminal sizes into one reusable "the window has settled at a
/// new size" signal. Dragging a window edge produces dozens of intermediate sizes, and
/// repainting into a half-built window is what corrupts the pinned layout: every intermediate
/// size is ignored, and a change is only reported once the same size has held steady.
/// </summary>
public sealed class TuiResizeWatcher
{
    public const int DefaultSettleMilliseconds = 350;

    private readonly int _settleMilliseconds;
    private int _candidateColumns;
    private int _candidateRows;
    private long _candidateSince;
    private int _appliedColumns = -1;
    private int _appliedRows = -1;
    private bool _hasCandidate;

    public TuiResizeWatcher(int settleMilliseconds = DefaultSettleMilliseconds)
    {
        _settleMilliseconds = Math.Max(0, settleMilliseconds);
    }

    /// <summary>Columns of the last size reported by <see cref="Observe"/>.</summary>
    public int AppliedColumns => _appliedColumns;

    /// <summary>Rows of the last size reported by <see cref="Observe"/>.</summary>
    public int AppliedRows => _appliedRows;

    /// <summary>
    /// Reports the current terminal size. Returns true exactly once per settled change: the
    /// first observation applies immediately, later ones must hold still for the settle window.
    /// Non-positive sizes (transient while the window is being rebuilt) are ignored.
    /// </summary>
    public bool Observe(int columns, int rows, long nowMilliseconds)
    {
        if (columns < 1 || rows < 1)
        {
            return false;
        }

        if (!_hasCandidate)
        {
            _hasCandidate = true;
            _candidateColumns = columns;
            _candidateRows = rows;
            _candidateSince = nowMilliseconds;
            _appliedColumns = columns;
            _appliedRows = rows;
            return true;
        }

        if (columns != _candidateColumns || rows != _candidateRows)
        {
            // Still moving: restart the settle clock and stay quiet.
            _candidateColumns = columns;
            _candidateRows = rows;
            _candidateSince = nowMilliseconds;
            return false;
        }

        if (columns == _appliedColumns && rows == _appliedRows)
        {
            return false;
        }

        if (nowMilliseconds - _candidateSince < _settleMilliseconds)
        {
            return false;
        }

        _appliedColumns = columns;
        _appliedRows = rows;
        return true;
    }
}
