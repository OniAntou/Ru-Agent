using System;

namespace RuApp.Services;

public readonly record struct SubtitleSnapshot(string Text)
{
    public static readonly SubtitleSnapshot Empty = new("");
}

public sealed class SubtitlePresenter : IDisposable
{
    private readonly object _gate = new();
    private string? _activeSegmentId;
    private SubtitleSnapshot _current = SubtitleSnapshot.Empty;

    public event Action<SubtitleSnapshot>? SnapshotChanged;
    public SubtitleSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void BeginSegment(string segmentId, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(segmentId);
        var snapshot = new SubtitleSnapshot(text);
        Action<SubtitleSnapshot>? changed;
        lock (_gate)
        {
            _activeSegmentId = segmentId;
            _current = snapshot;
            changed = SnapshotChanged;
        }
        changed?.Invoke(snapshot);
    }

    public void EndSegment(string segmentId)
    {
        Action<SubtitleSnapshot>? changed;
        lock (_gate)
        {
            if (!string.Equals(_activeSegmentId, segmentId, StringComparison.Ordinal))
            {
                return;
            }
            _activeSegmentId = null;
            _current = SubtitleSnapshot.Empty;
            changed = SnapshotChanged;
        }
        changed?.Invoke(SubtitleSnapshot.Empty);
    }

    public void CancelAll()
    {
        Action<SubtitleSnapshot>? changed;
        lock (_gate)
        {
            _activeSegmentId = null;
            _current = SubtitleSnapshot.Empty;
            changed = SnapshotChanged;
        }
        changed?.Invoke(SubtitleSnapshot.Empty);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _activeSegmentId = null;
            _current = SubtitleSnapshot.Empty;
            SnapshotChanged = null;
        }
    }
}
