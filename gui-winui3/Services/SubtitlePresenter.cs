using System;

namespace RuApp.Services;

public readonly record struct SubtitleSnapshot(string Text)
{
    public static readonly SubtitleSnapshot Empty = new("");
}

public sealed class SubtitlePresenter : IDisposable
{
    public event Action<SubtitleSnapshot>? SnapshotChanged;
    public SubtitleSnapshot Current { get; private set; } = SubtitleSnapshot.Empty;

    public void BeginSegment(string text, int durationMs)
    {
        Current = new SubtitleSnapshot(text);
        SnapshotChanged?.Invoke(Current);
    }

    public void CancelAll()
    {
        Current = SubtitleSnapshot.Empty;
        SnapshotChanged?.Invoke(Current);
    }

    public void Dispose() => SnapshotChanged = null;
}