using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuApp.Services;
using Windows.UI.ViewManagement;

namespace RuApp.Controls;

public sealed partial class WelcomeOverlay
{
    private readonly TextBlock _brailleText = new()
    {
        FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 18,
        LineHeight = 22, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        TextWrapping = TextWrapping.NoWrap, IsTextSelectionEnabled = false,
    };
    private Viewbox? _brailleHost;
    private readonly TextBlock _distantBrailleText = new()
    {
        FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 18,
        LineHeight = 22, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        TextWrapping = TextWrapping.NoWrap, IsTextSelectionEnabled = false,
    };
    private Viewbox? _distantBrailleHost;
    private double _lastDistantFrameAt = double.NegativeInfinity;
    private DispatcherQueueTimer? _brailleTimer;
    private int _brailleColumns = 96, _brailleRows = 36;

    private void InitializeBraille()
    {
        InitializeAtmosphere();
        _distantBrailleHost = new Viewbox
        {
            Child = _distantBrailleText, Stretch = Stretch.Fill,
            IsHitTestVisible = false, Opacity = 0.10,
        };
        AutomationProperties.SetAccessibilityView(_distantBrailleHost, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_distantBrailleText, AccessibilityView.Raw);
        Children.Add(_distantBrailleHost);
        _brailleHost = new Viewbox
        {
            Child = _brailleText, Stretch = Stretch.Fill,
            IsHitTestVisible = false, Opacity = 0.38,
        };
        AutomationProperties.SetAccessibilityView(_brailleHost, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_brailleText, AccessibilityView.Raw);
        Children.Add(_brailleHost);
        RenderBrailleFrame(0);
    }

    private void ApplyBrailleTheme()
    {
        var green = ((SolidColorBrush)ThemeBrush("RuBrandGreenBrush")).Color;
        var mint = ((SolidColorBrush)ThemeBrush("RuSuccessBrush")).Color;
        _brailleText.Foreground = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.2),
            EndPoint = new Windows.Foundation.Point(1, 0.9),
            GradientStops =
            {
                new GradientStop { Offset = 0, Color = green },
                new GradientStop { Offset = 0.55, Color = mint },
                new GradientStop { Offset = 1, Color = green },
            },
        };
        if (_brailleHost is null) return;
        _brailleHost.Visibility = new AccessibilitySettings().HighContrast
            ? Visibility.Collapsed : Visibility.Visible;
        _brailleHost.Opacity = ActualTheme == ElementTheme.Light ? 0.22 : 0.36;
        if (_distantBrailleHost is not null)
        {
            _distantBrailleHost.Visibility = _brailleHost.Visibility;
            _distantBrailleHost.Opacity = ActualTheme == ElementTheme.Light ? 0.07 : 0.10;
        }
        _distantBrailleText.Foreground = new SolidColorBrush(green);
        ApplyAtmosphereTheme();
    }

    private void ResizeBraille(double width, double height)
    {
        int columns = Math.Clamp((int)Math.Min(128, Math.Ceiling(width / 13)), 1, 128);
        int rows = Math.Clamp((int)Math.Min(56, Math.Ceiling(height / 22)), 1, 56);
        if (columns == _brailleColumns && rows == _brailleRows) return;
        _brailleColumns = columns;
        _brailleRows = rows;
        _lastDistantFrameAt = double.NegativeInfinity;
        RenderBrailleFrame(_motionEnabled ? _clock.Elapsed.TotalSeconds : 0);
    }

    private void StartBrailleMotion()
    {
        if (_disposed || _isReady || _isDismissed || !IsLoaded || !UiTiming.AnimationsEnabled) return;
        _brailleTimer ??= DispatcherQueue.CreateTimer();
        _brailleTimer.Interval = TimeSpan.FromMilliseconds(50);
        _brailleTimer.Tick -= OnBrailleTick;
        _brailleTimer.Tick += OnBrailleTick;
        _brailleTimer.Start();
    }

    private void OnBrailleTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _isReady || _isDismissed || !IsLoaded || !UiTiming.AnimationsEnabled)
        {
            StopBrailleMotion(settle: true);
            return;
        }
        if (_brailleHost?.Visibility == Visibility.Visible) RenderBrailleFrame(_clock.Elapsed.TotalSeconds);
    }

    private void RenderBrailleFrame(double seconds)
    {
        string frame = StartupBrailleWave.Frame(_brailleColumns, _brailleRows, seconds);
        if (!string.Equals(_brailleText.Text, frame, StringComparison.Ordinal)) _brailleText.Text = frame;
        // The dimmer field drifts out of phase at half speed and only needs 10 Hz.
        if (seconds == 0 || Math.Abs(seconds - _lastDistantFrameAt) >= 0.1)
        {
            string distant = StartupBrailleWave.Frame(_brailleColumns, _brailleRows, seconds * 0.52 + 17);
            if (!string.Equals(_distantBrailleText.Text, distant, StringComparison.Ordinal))
                _distantBrailleText.Text = distant;
            _lastDistantFrameAt = seconds;
        }
    }

    private void StopBrailleMotion(bool settle)
    {
        _brailleTimer?.Stop();
        if (settle) RenderBrailleFrame(0);
    }

    private void DisposeBraille()
    {
        _brailleTimer?.Stop();
        if (_brailleTimer is not null) _brailleTimer.Tick -= OnBrailleTick;
        _brailleTimer = null;
        DisposeAtmosphere();
    }
}
