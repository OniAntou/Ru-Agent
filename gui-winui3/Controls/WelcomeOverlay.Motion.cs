using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using RuApp.Services;

namespace RuApp.Controls;

public sealed partial class WelcomeOverlay
{
    public const double ExitMilliseconds = 180;
    private readonly Stopwatch _clock = new();
    private DispatcherQueueTimer? _watchTimer;
    private Storyboard? _brandEntrance;
    private Storyboard? _brandSweep;
    private Storyboard? _exitAnimation;
    private TaskCompletionSource<bool>? _exitCompletion;
    private bool _motionEnabled;
    private bool _entrancePlayed;

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_disposed || _isDismissed) return;
        BindViewportRoot();
        if (_isReady)
        {
            if (_hasError) _continueButton.Focus(FocusState.Programmatic);
            return;
        }
        _clock.Start();
        _motionEnabled = UiTiming.AnimationsEnabled;
        if (_motionEnabled && !_entrancePlayed) PlayBrandEntrance();
        if (_motionEnabled) StartLoadingSweep();
        _watchTimer ??= DispatcherQueue.CreateTimer();
        _watchTimer.Interval = TimeSpan.FromMilliseconds(250);
        _watchTimer.Tick -= OnWatchTick;
        _watchTimer.Tick += OnWatchTick;
        _watchTimer.Start();
    }

    private void OnWatchTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _isDismissed || _isReady) { sender.Stop(); return; }
        bool enabled = UiTiming.AnimationsEnabled;
        if (enabled != _motionEnabled)
        {
            _motionEnabled = enabled;
            StopBrandMotion(settle: true);
            if (enabled) StartLoadingSweep();
        }
        if (_clock.Elapsed >= TimeSpan.FromSeconds(8)) _slowHint.Visibility = Visibility.Visible;
    }

    private void PlayBrandEntrance()
    {
        if (!UiTiming.AnimationsEnabled) return;
        _entrancePlayed = true;
        var fade = new DoubleAnimation { From = 0.65, To = 1, Duration = TimeSpan.FromMilliseconds(600) };
        Storyboard.SetTarget(fade, _brandHost);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var rise = new DoubleAnimation
        {
            From = 6, To = 0, Duration = TimeSpan.FromMilliseconds(650),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(rise, _brandSlide);
        Storyboard.SetTargetProperty(rise, "Y");
        _brandEntrance = new Storyboard();
        _brandEntrance.Children.Add(fade);
        _brandEntrance.Children.Add(rise);
        _brandEntrance.Begin();
    }

    private void StartLoadingSweep()
    {
        if (_disposed || _isReady || _isDismissed || !UiTiming.AnimationsEnabled) return;
        _brandSweep?.Stop();
        var pulse = new DoubleAnimation
        {
            From = -36, To = 120, Duration = TimeSpan.FromMilliseconds(1600),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(pulse, _accentSlide);
        Storyboard.SetTargetProperty(pulse, "X");
        _brandSweep = new Storyboard();
        _brandSweep.Children.Add(pulse);
        _brandSweep.Begin();
        StartBrailleMotion();
    }

    private void StopBrandMotion(bool settle)
    {
        StopBrailleMotion(settle);
        double opacity = _brandHost.Opacity, offset = _brandSlide.Y, accentOffset = _accentSlide.X;
        _brandEntrance?.Stop();
        _brandSweep?.Stop();
        _brandEntrance = _brandSweep = null;
        _brandHost.Opacity = settle ? 1 : opacity;
        _brandSlide.Y = settle ? 0 : offset;
        _accentSlide.X = settle ? 42 : accentOffset;
    }

    private Task PlayExitAsync()
    {
        if (_isDismissed || _disposed) return Task.CompletedTask;
        if (_exitCompletion is not null) return _exitCompletion.Task;
        _exitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _watchTimer?.Stop();
        _clock.Stop();
        StopBrandMotion(settle: false);
        if (!UiTiming.AnimationsEnabled || !IsLoaded)
        {
            CompleteDismiss();
            return _exitCompletion.Task;
        }
        var fade = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(ExitMilliseconds) };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var animation = new Storyboard();
        animation.Children.Add(fade);
        animation.Completed += (_, _) =>
        {
            if (ReferenceEquals(_exitAnimation, animation)) CompleteDismiss();
        };
        _exitAnimation = animation;
        animation.Begin();
        return _exitCompletion.Task;
    }

    private void CompleteDismiss()
    {
        if (_isDismissed) return;
        _isDismissed = true;
        _exitAnimation?.Stop();
        _exitAnimation = null;
        Visibility = Visibility.Collapsed;
        _exitCompletion?.TrySetResult(true);
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        UnbindViewportRoot();
        _watchTimer?.Stop();
        _clock.Stop();
        StopBrandMotion(settle: true);
        if (_exitCompletion is not null) CompleteDismiss();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnbindViewportRoot();
        _watchTimer?.Stop();
        if (_watchTimer is not null) _watchTimer.Tick -= OnWatchTick;
        _watchTimer = null;
        _clock.Stop();
        StopBrandMotion(settle: true);
        CompleteDismiss();
        DisposeBraille();
    }
}
