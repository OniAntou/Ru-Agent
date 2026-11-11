using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using RuApp.Services;
using Windows.UI.ViewManagement;

namespace RuApp.Controls;

/// <summary>
/// The startup backdrop is the app's own meteor shower (<see cref="MeteorShowerField"/>), and
/// the timer here drives the colour wave on the wordmark.
/// </summary>
public sealed partial class WelcomeOverlay
{
    private readonly MeteorShowerField _meteors = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        IsHitTestVisible = false,
    };
    private DispatcherQueueTimer? _waveTimer;

    private void InitializeBackdrop()
    {
        AutomationProperties.SetAccessibilityView(_meteors, AccessibilityView.Raw);
        Children.Add(_meteors);
        InitializeVignette();
        RenderFrame(0);
    }

    private void ApplyBackdropTheme()
    {
        SyncMeteors();
        ApplyVignetteTheme();
    }

    // Meteors run while the screen is loading and animations are on. An error leaves the
    // backdrop still, and high contrast takes no decoration.
    private void SyncMeteors()
    {
        bool run = !_disposed && !_isDismissed && IsLoaded && !(_isReady && _hasError)
            && UiTiming.AnimationsEnabled && !new AccessibilitySettings().HighContrast;
        _meteors.IsActive = run;
    }

    private void StartWaveMotion()
    {
        if (_disposed || _isReady || _isDismissed || !IsLoaded || !UiTiming.AnimationsEnabled) return;
        _waveTimer ??= DispatcherQueue.CreateTimer();
        _waveTimer.Interval = TimeSpan.FromMilliseconds(50);
        _waveTimer.Tick -= OnWaveTick;
        _waveTimer.Tick += OnWaveTick;
        _waveTimer.Start();
    }

    private void OnWaveTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _isReady || _isDismissed || !IsLoaded || !UiTiming.AnimationsEnabled)
        {
            StopWaveMotion(settle: true);
            return;
        }

        RenderFrame(_clock.Elapsed.TotalSeconds);
    }

    private void RenderFrame(double seconds) => RenderWordmarkWave(seconds);

    private void StopWaveMotion(bool settle)
    {
        _waveTimer?.Stop();
        if (settle) RenderFrame(0);
    }

    private void DisposeBackdrop()
    {
        _waveTimer?.Stop();
        if (_waveTimer is not null) _waveTimer.Tick -= OnWaveTick;
        _waveTimer = null;
        _meteors.IsActive = false;
        _meteors.Dispose();
        DisposeVignette();
    }
}