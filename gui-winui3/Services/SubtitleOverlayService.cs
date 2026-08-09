using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using RuApp.Models;
using RuApp.Views;

namespace RuApp.Services;

public sealed class SubtitleOverlayService : IDisposable
{
    private readonly SubtitleOverlayWindow _window;
    private readonly VtsWindowTracker _tracker;
    private readonly SubtitlePresenter _presenter;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherTimer _trackerTimer;
    private readonly DispatcherTimer _fadeTimer;
    private readonly SubtitleFadeState _fadeState = new();
    private readonly SubtitlePlacementCache _placementCache = new();

    private bool _started;
    private bool _disposed;
    private bool _vtsHidden = true;

    public SubtitleOverlayService(SubtitleOverlayWindow window, VtsWindowTracker tracker, SubtitlePresenter presenter)
    {
        _window = window;
        _tracker = tracker;
        _presenter = presenter;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Subtitle overlay must be created on the UI thread.");

        _trackerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _trackerTimer.Tick += (_, __) => PollTracker();

        _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _fadeTimer.Tick += (_, __) =>
        {
            _fadeTimer.Stop();
            ApplyFadeAction(_fadeState.OnDelayElapsed(), SubtitleSnapshot.Empty);
        };

        _presenter.SnapshotChanged += OnPresenterSnapshotChanged;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _started = true;

        _trackerTimer.Start();
        PollTracker();
    }

    public void Pause()
    {
        if (_disposed || !_started) return;
        _started = false;

        _trackerTimer.Stop();
        _fadeTimer.Stop();
        _window.CancelFadeOut();
        _window.HideOverlay();
        _fadeState.Reset();
    }

    public void Stop()
    {
        if (_disposed) return;
        _disposed = true;
        _started = false;

        _trackerTimer.Stop();
        _fadeTimer.Stop();
        _window.CancelFadeOut();
        _fadeState.Reset();
        _window.HideOverlay();
        _window.Close();

        _presenter.SnapshotChanged -= OnPresenterSnapshotChanged;
        _tracker.Dispose();
    }

    public void Dispose() => Stop();

    private void PollTracker()
    {
        if (!_started || _disposed) return;
        VtsWindowSnapshot snapshot = _tracker.GetCurrent();
        if (!snapshot.Visible)
        {
            _window.HideOverlay();
            _window.CancelFadeOut();
            _fadeTimer.Stop();
            _fadeState.Reset();
            _vtsHidden = true;
            return;
        }

        bool becameVisible = _vtsHidden;
        _vtsHidden = false;
        (RectPx rect, int physicalFontSize, bool _) = SubtitleLayout.ComputeSubtitleRect(
            snapshot.Rect, snapshot.WorkArea);
        int fontSize = SubtitleLayout.ToEffectiveFontSize(physicalFontSize, snapshot.Dpi);
        if (_placementCache.ShouldApply(rect, fontSize))
        {
            _window.MoveAndResize(rect, fontSize);
        }

        SubtitleSnapshot current = _presenter.Current;
        if (becameVisible && current != SubtitleSnapshot.Empty)
        {
            ApplyFadeAction(_fadeState.OnTextAvailable(), current);
        }
    }

    private void OnPresenterSnapshotChanged(SubtitleSnapshot snapshot)
    {
        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => OnPresenterSnapshotChanged(snapshot));
            return;
        }
        if (!_started || _disposed) return;

        if (snapshot == SubtitleSnapshot.Empty)
        {
            ApplyFadeAction(_fadeState.OnTextCleared(), snapshot);
            return;
        }

        if (_vtsHidden) return;
        ApplyFadeAction(_fadeState.OnTextAvailable(), snapshot);
    }

    private void ApplyFadeAction(SubtitleFadeAction action, SubtitleSnapshot snapshot)
    {
        switch (action)
        {
            case SubtitleFadeAction.Show:
            case SubtitleFadeAction.CancelFadeAndShow:
                _fadeTimer.Stop();
                _window.CancelFadeOut();
                _window.ApplySnapshot(snapshot);
                _window.ShowOverlay();
                break;
            case SubtitleFadeAction.StartDelay:
                _fadeTimer.Start();
                break;
            case SubtitleFadeAction.BeginFade:
                _window.BeginFadeOut(OnFadeCompleted);
                break;
            case SubtitleFadeAction.Hide:
                _window.ApplySnapshot(SubtitleSnapshot.Empty);
                _window.HideOverlay();
                break;
        }
    }

    private void OnFadeCompleted()
    {
        ApplyFadeAction(_fadeState.OnFadeCompleted(), SubtitleSnapshot.Empty);
    }
}
