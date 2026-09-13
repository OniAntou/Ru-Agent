using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using RuApp.Models;
using RuApp.Views;

namespace RuApp.Services;

public sealed class SubtitleOverlayService : IDisposable
{
    private readonly SubtitleOverlayWindow _window;
    private readonly SubtitlePresenter _presenter;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherTimer _trackerTimer;
    private readonly DispatcherTimer _fadeTimer;
    private readonly SubtitleFadeState _fadeState = new();
    private readonly SubtitlePlacementCache _placementCache = new();

    private bool _started;
    private bool _disposed;

    public event Action<int, int>? PositionChanged;

    public string PosMode { get; set; } = "auto";
    public int CustomX { get; set; } = -1;
    public int CustomY { get; set; } = -1;

    public SubtitleOverlayService(SubtitleOverlayWindow window, SubtitlePresenter presenter)
    {
        _window = window;
        _presenter = presenter;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Subtitle overlay must be created on the UI thread.");

        _window.PositionMoved += (x, y) =>
        {
            CustomX = x;
            CustomY = y;
            PosMode = "custom";
            PositionChanged?.Invoke(x, y);
        };

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

    public void SetDragMode(bool enableDrag)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (enableDrag && !_started)
            {
                Start();
            }
            _window.SetDraggable(enableDrag);
        });
    }

    public void ResetPosition()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            PosMode = "auto";
            CustomX = -1;
            CustomY = -1;
            _placementCache.Reset();
            PollTracker();
        });
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _started = true;

        _trackerTimer.Start();
        PollTracker();
    }

    public void ApplyCustomization(
        string? color,
        string? size,
        double durationSeconds,
        string? posMode = null,
        int customX = -1,
        int customY = -1)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (!string.IsNullOrWhiteSpace(color))
            {
                _window.SetTextColor(color);
            }
            if (!string.IsNullOrWhiteSpace(size))
            {
                _window.SetSizeCategory(size);
            }
            if (durationSeconds >= 1.0 && durationSeconds <= 10.0)
            {
                _fadeTimer.Interval = TimeSpan.FromSeconds(durationSeconds);
            }
            if (!string.IsNullOrWhiteSpace(posMode))
            {
                PosMode = posMode;
            }
            if (customX >= 0) CustomX = customX;
            if (customY >= 0) CustomY = customY;

            if (PosMode == "custom" && CustomX >= 0 && CustomY >= 0)
            {
                _window.SetPositionOnly(CustomX, CustomY);
            }
        });
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
    }

    public void Dispose() => Stop();

    private void PollTracker()
    {
        if (!_started || _disposed || _window.IsDraggable) return;

        if (PosMode == "custom" && CustomX >= 0 && CustomY >= 0)
        {
            GetPrimaryWorkArea(out int dpi);
            int fontSize = SubtitleLayout.ToEffectiveFontSize(24, dpi);
            var customRect = new RectPx(CustomX, CustomY, 600, 80);
            if (_placementCache.ShouldApply(customRect, fontSize))
            {
                _window.MoveAndResize(customRect, fontSize);
            }
        }
        else
        {
            RectPx workArea = GetPrimaryWorkArea(out int dpi);
            int fallbackWidth = Math.Clamp(workArea.Width / 2, 260, 640);
            int fallbackHeight = 200;
            int x = workArea.X + (workArea.Width - fallbackWidth) / 2;
            int y = workArea.Bottom - fallbackHeight - 40;
            RectPx anchorRect = new RectPx(x, y, fallbackWidth, fallbackHeight);

            (RectPx rect, int physicalFontSize, bool _) = SubtitleLayout.ComputeSubtitleRect(
                anchorRect, workArea);
            int fontSize = SubtitleLayout.ToEffectiveFontSize(physicalFontSize, dpi);
            if (_placementCache.ShouldApply(rect, fontSize))
            {
                _window.MoveAndResize(rect, fontSize);
            }
        }

        _window.EnsureTopmost();
        SubtitleSnapshot current = _presenter.Current;
        if (current != SubtitleSnapshot.Empty)
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

    private static RectPx GetPrimaryWorkArea(out int dpi)
    {
        dpi = 96;
        try
        {
            IntPtr monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, 2);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { Size = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    uint sysDpi = GetDpiForSystem();
                    if (sysDpi > 0) dpi = (int)sysDpi;
                    return new RectPx(
                        info.WorkArea.Left,
                        info.WorkArea.Top,
                        info.WorkArea.Right - info.WorkArea.Left,
                        info.WorkArea.Bottom - info.WorkArea.Top);
                }
            }
        }
        catch
        {
        }
        return new RectPx(0, 0, 1920, 1080);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public uint Size; public RECT Monitor; public RECT WorkArea; public uint Flags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
