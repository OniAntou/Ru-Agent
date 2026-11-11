namespace RuApp.Services;

/// <summary>Where the reader was: a screen and, on the chat screen, the session it showed.</summary>
public readonly record struct NavLocation(ShellDestination Destination, string? SessionId);

/// <summary>
/// Back and forward through the places the reader has been, like a browser. Visiting
/// somewhere new drops whatever lay ahead; visiting the place already shown changes
/// nothing. Entries that no longer exist (a deleted session) are skipped and removed
/// when a move reaches them. Free of UI types so the rules are tested without a window.
/// </summary>
public sealed class NavigationHistory<T> where T : notnull
{
    private readonly List<T> _entries = [];
    private readonly IEqualityComparer<T> _comparer;
    private readonly int _capacity;
    private int _index = -1;

    public NavigationHistory(int capacity = 50, IEqualityComparer<T>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _capacity = capacity;
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    public int Count => _entries.Count;

    public bool CanGoBack => _index > 0;

    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;

    public bool HasCurrent => _index >= 0;

    public T Current => _index >= 0 ? _entries[_index] : throw new InvalidOperationException("The history is empty.");

    /// <summary>Notes a visit. The place already shown is not a new visit.</summary>
    public void Record(T entry)
    {
        if (_index >= 0 && _comparer.Equals(_entries[_index], entry)) return;
        if (_index < _entries.Count - 1) _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        _entries.Add(entry);
        if (_entries.Count > _capacity) _entries.RemoveAt(0);
        _index = _entries.Count - 1;
    }

    /// <summary>
    /// Moves one step back (<paramref name="direction"/> below zero) or forward, past
    /// any entry <paramref name="isValid"/> rejects; rejected entries are removed.
    /// Returns false, leaving the position alone, when nothing valid lies that way.
    /// </summary>
    public bool TryMove(int direction, Func<T, bool> isValid, out T entry)
    {
        ArgumentNullException.ThrowIfNull(isValid);
        int step = direction < 0 ? -1 : 1;
        int next = _index + step;
        while (next >= 0 && next < _entries.Count)
        {
            if (isValid(_entries[next]))
            {
                _index = next;
                entry = _entries[next];
                return true;
            }

            _entries.RemoveAt(next);
            if (step < 0)
            {
                // Everything from the removed slot on shifted down by one.
                _index--;
                next = _index - 1;
            }
        }

        entry = default!;
        return false;
    }
}
