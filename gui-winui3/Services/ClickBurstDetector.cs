namespace RuApp.Services;

/// <summary>
/// Notices a burst of clicks: <c>threshold</c> clicks inside a sliding <c>window</c>. Used by the
/// New Chat black hole easter egg; after it fires the count starts over.
/// </summary>
internal sealed class ClickBurstDetector(int threshold, TimeSpan window)
{
    private readonly Queue<TimeSpan> _clicks = new();

    /// <summary>Records a click at <paramref name="now"/>; true when it completes a burst.</summary>
    public bool Register(TimeSpan now)
    {
        _clicks.Enqueue(now);
        while (_clicks.Count > 0 && now - _clicks.Peek() > window) _clicks.Dequeue();
        if (_clicks.Count < threshold) return false;
        _clicks.Clear();
        return true;
    }
}
