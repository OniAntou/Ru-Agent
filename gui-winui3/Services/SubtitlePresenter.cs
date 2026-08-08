using System;
using System.Collections.Generic;
using System.Linq;

namespace RuApp.Services;

public readonly record struct SubtitleSnapshot(
    string VisibleText, string HighlightToken, int HighlightStartMs, int HighlightEndMs)
{
    public static readonly SubtitleSnapshot Empty = new("", "", 0, 0);
}

public sealed class SubtitlePresenter : IDisposable
{
    private IReadOnlyList<SubtitleToken> _tokens = Array.Empty<SubtitleToken>();
    private int _index;
    private int _elapsedMs;

    public event Action<SubtitleSnapshot>? SnapshotChanged;
    public SubtitleSnapshot Current { get; private set; } = SubtitleSnapshot.Empty;
    public bool IsSpeaking => _tokens.Count > 0;

    public void BeginSegment(string text, int durationMs)
    {
        _tokens = SubtitleTiming.TokenizeWithTiming(text, durationMs).Select(
            t => new SubtitleToken(t.Token, t.StartMs, t.EndMs)).ToArray();
        _index = 0;
        _elapsedMs = 0;
        Emit();
    }

    public void CancelAll()
    {
        _tokens = Array.Empty<SubtitleToken>();
        _index = 0;
        _elapsedMs = 0;
        Current = SubtitleSnapshot.Empty;
        SnapshotChanged?.Invoke(Current);
    }

    public void Tick(int elapsedMs)
    {
        if (_tokens.Count == 0 || elapsedMs < _elapsedMs) return;
        _elapsedMs = elapsedMs;
        if (_index < _tokens.Count - 1 && elapsedMs >= _tokens[_index + 1].EndMs)
        {
            _index++;
            Emit();
        }
    }

    private void Emit()
    {
        if (_tokens.Count == 0) return;
        var token = _tokens[_index];
        Current = new SubtitleSnapshot(
            string.Join(" ", _tokens.Select(t => t.Text)),
            token.Text, token.StartMs, token.EndMs);
        SnapshotChanged?.Invoke(Current);
    }

    public void Dispose() => SnapshotChanged = null;

    private readonly record struct SubtitleToken(string Text, int StartMs, int EndMs);
}