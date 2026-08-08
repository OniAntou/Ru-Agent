using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using RuApp.Models;
using RuApp.Views;

namespace RuApp.Services;

public sealed class SubtitleOverlayService : IDisposable
{
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private readonly SubtitleOverlayWindow _window;
    private readonly VtsWindowTracker _tracker;
    private readonly SubtitlePresenter _presenter;
    private readonly DispatcherTimer _trackerTimer;
    private readonly DispatcherTimer _presenterTimer;
    private readonly DispatcherTimer _fadeTimer;
    private readonly Stopwatch _elapsed = new();

    private bool _started;
    private bool _windowVisible;
    private bool _vtsHidden;
    private bool _fading;
    private SubtitleSnapshot _lastSnapshot = SubtitleSnapshot.Empty;

    public SubtitleOverlayService(SubtitleOverlayWindow window, VtsWindowTracker tracker, SubtitlePresenter presenter)
    {
        _window = window;
        _tracker = tracker;
        _presenter = presenter;

        _trackerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _trackerTimer.Tick += (_, __) => PollTracker();

        _presenterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _presenterTimer.Tick += (_, __) => TickPresenter();

        _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _fadeTimer.Tick += (_, __) =>
        {
            _fadeTimer.Stop();
            _window.BeginFadeOut(() =>
            {
                _fading = false;
                _windowVisible = false;
            });
        };

        _presenter.SnapshotChanged += OnPresenterSnapshotChanged;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        _window.ShowOverlay();
        _windowVisible = true;
        _trackerTimer.Start();
        _presenterTimer.Start();
        PollTracker();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;

        _trackerTimer.Stop();
        _presenterTimer.Stop();
        _fadeTimer.Stop();
        _elapsed.Stop();
        _window.CancelFadeOut();
        _fading = false;
        _window.Close();

        _presenter.SnapshotChanged -= OnPresenterSnapshotChanged;
    }

    public void Dispose() => Stop();

    private void PollTracker()
    {
        VtsWindowSnapshot snapshot = _tracker.GetCurrent();
        if (!snapshot.Visible)
        {
            _window.HideOverlay();
            _windowVisible = false;
            _vtsHidden = true;
            return;
        }

        _vtsHidden = false;
        (RectPx rect, int fontSize, bool _) = SubtitleLayout.ComputeSubtitleRect(
            snapshot.Rect, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
        _window.MoveAndResize(rect, fontSize);
    }

    private void TickPresenter()
    {
        if (!_elapsed.IsRunning) return;
        _presenter.Tick((int)_elapsed.ElapsedMilliseconds);
    }

    private void OnPresenterSnapshotChanged(SubtitleSnapshot snapshot)
    {
        _lastSnapshot = snapshot;

        if (snapshot == SubtitleSnapshot.Empty)
        {
            _elapsed.Stop();
            if (_windowVisible)
            {
                _fadeTimer.Start();
            }
            return;
        }

        if (!_elapsed.IsRunning)
        {
            _elapsed.Restart();
        }

        _fadeTimer.Stop();
        if (_fading)
        {
            _window.CancelFadeOut();
            _fading = false;
        }
        if (_vtsHidden) return;
        _window.ShowOverlay();
        _windowVisible = true;
        _window.ApplySnapshot(snapshot);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
