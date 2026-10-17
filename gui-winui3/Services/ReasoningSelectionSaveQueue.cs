namespace RuApp.Services;

/// <summary>Serializes writes and keeps a confirmed fallback per active model selection.</summary>
internal sealed class ReasoningSelectionSaveQueue
{
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly UiRequestLifetime _edits = new();
    private string _context = string.Empty;
    private string _confirmed = "default";
    private long _contextVersion;
    internal bool IsPending { get; private set; }
    internal string ConfirmedEffort => _confirmed;

    internal void Synchronize(string context, string confirmed)
    {
        if (_context == context) return;
        if (_context != context) Invalidate();
        _context = context;
        _confirmed = confirmed;
    }

    internal void Invalidate()
    {
        _edits.Replace();
        _contextVersion++;
        IsPending = false;
        _context = string.Empty;
    }

    internal async Task<string?> SaveAsync(string context, string value, string previous, Func<Task<bool>> save)
    {
        if (_context != context) Synchronize(context, previous);
        var edit = _edits.Replace();
        var contextVersion = _contextVersion;
        IsPending = true;
        await _writes.WaitAsync();
        try
        {
            if (!_edits.Owns(edit) || _context != context) return null;
            bool saved;
            try { saved = await save(); }
            catch (Exception) { saved = false; }
            // A completed write is the fallback even when a newer edit is waiting.
            if (_contextVersion == contextVersion && saved) _confirmed = value;
        }
        finally { _writes.Release(); }
        if (!_edits.Owns(edit) || _context != context) return null;
        IsPending = false;
        return _confirmed;
    }
}
