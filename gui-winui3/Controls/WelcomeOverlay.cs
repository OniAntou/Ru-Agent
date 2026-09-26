using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace RuApp.Controls;

// THESIS: a boot screen that reads as a local runtime console, not a SaaS hero.
// It refuses the centered gradient hero every generator ships.
// OWN-WORLD: slate tokens only; hairline frame with corner ticks; one cropped
// giant glyph with a hard offset echo; Consolas micro-readouts; a single exit
// transition and no looping motion anywhere.
// STORY: the visitor sees the machine waking up, reads the live boot status,
// and is carried into the app automatically when the backend is up.
// FIRST VIEWPORT: bottom-left block (kicker, 56pt wordmark, live mono status,
// hairline progress) over a giant cropped "Ru"; hairline frame insets the edge.
// FORM: committed replacement of the centered overlay; entrance is static, the
// exit slide-and-fade is the one authored moment.
public sealed class WelcomeOverlay : Grid, IDisposable
{
    private readonly Border _frame;
    private readonly XamlPath[] _cornerTicks = new XamlPath[4];
    private readonly TextBlock _giantEcho;
    private readonly TextBlock _giantGlyph;
    private readonly StackPanel _content;
    private readonly TranslateTransform _contentTransform = new();
    private readonly TextBlock _statusLabel;
    private readonly ProgressBar _bootProgress;

    private bool _isDismissed;
    private bool _isReady;
    private bool _exitStarted;

    public event EventHandler? Dismissed;

    public bool IsDismissed => _isDismissed;
    public bool IsReady => _isReady;

    public WelcomeOverlay()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        ApplyThemeBackground();

        // Hairline frame insetting the viewport edge.
        _frame = new Border
        {
            Margin = new Thickness(24),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false
        };
        Children.Add(_frame);

        // Corner ticks registering the frame like a console bezel.
        var tickAlignments = new (HorizontalAlignment, VerticalAlignment)[]
        {
            (HorizontalAlignment.Left, VerticalAlignment.Top),
            (HorizontalAlignment.Right, VerticalAlignment.Top),
            (HorizontalAlignment.Left, VerticalAlignment.Bottom),
            (HorizontalAlignment.Right, VerticalAlignment.Bottom),
        };
        for (int i = 0; i < _cornerTicks.Length; i++)
        {
            var figure = new PathFigure
            {
                StartPoint = new Windows.Foundation.Point(0, 14),
                IsClosed = false,
                IsFilled = false
            };
            figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(0, 0) });
            figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(14, 0) });
            var tickGeometry = new PathGeometry();
            tickGeometry.Figures.Add(figure);

            var tick = new XamlPath
            {
                Data = tickGeometry,
                StrokeThickness = 2,
                Width = 14,
                Height = 14,
                HorizontalAlignment = tickAlignments[i].Item1,
                VerticalAlignment = tickAlignments[i].Item2,
                Margin = new Thickness(24),
                IsHitTestVisible = false
            };
            _cornerTicks[i] = tick;
            Children.Add(tick);
        }

        // Cropped giant glyph with a hard offset echo behind the content.
        _giantEcho = new TextBlock
        {
            Text = "Ru",
            FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
            FontSize = 200,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            CharacterSpacing = -60,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(-24, 0, 0, -62),
            IsHitTestVisible = false
        };
        _giantGlyph = new TextBlock
        {
            Text = "Ru",
            FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
            FontSize = 200,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            CharacterSpacing = -60,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(-30, 0, 0, -56),
            IsHitTestVisible = false
        };
        Children.Add(_giantEcho);
        Children.Add(_giantGlyph);

        // Bottom-left console block.
        _content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            MaxWidth = 640,
            Margin = new Thickness(64, 0, 64, 88),
            RenderTransform = _contentTransform
        };

        var kicker = new TextBlock
        {
            Text = "LOCAL RUNTIME",
            FontFamily = (FontFamily)Application.Current.Resources["RuMonoFontFamily"],
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 240,
            Margin = new Thickness(0, 0, 0, 14)
        };
        kicker.Foreground = new SolidColorBrush(ThemeColor("RuMutedBrush", Color.FromArgb(255, 148, 163, 184)));
        _content.Children.Add(kicker);

        var title = new TextBlock
        {
            Text = "Ru Agent",
            FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
            FontSize = 56,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            CharacterSpacing = -28,
            Margin = new Thickness(0, 0, 0, 12)
        };
        title.Foreground = new SolidColorBrush(ThemeColor("RuInkBrush", Color.FromArgb(255, 241, 245, 249)));
        _content.Children.Add(title);

        _statusLabel = new TextBlock
        {
            Text = "Starting system...",
            FontFamily = (FontFamily)Application.Current.Resources["RuMonoFontFamily"],
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 16)
        };
        _statusLabel.Foreground = new SolidColorBrush(ThemeColor("RuMutedBrush", Color.FromArgb(255, 148, 163, 184)));
        _content.Children.Add(_statusLabel);

        _bootProgress = new ProgressBar
        {
            IsIndeterminate = true,
            Width = 240,
            Height = 3,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        ApplyProgressTheme();
        _content.Children.Add(_bootProgress);

        Children.Add(_content);

        ApplyFrameTheme();
        ApplyGiantTheme();

        ActualThemeChanged += (_, _) =>
        {
            ApplyThemeBackground();
            ApplyFrameTheme();
            ApplyGiantTheme();
            ApplyProgressTheme();
        };

        KeyDown += (_, e) =>
        {
            if (_isReady && !_isDismissed
                && (e.Key == Windows.System.VirtualKey.Enter || e.Key == Windows.System.VirtualKey.Space))
            {
                _ = PlayExitAsync();
            }
        };
    }

    private bool IsDarkTheme() =>
        ActualTheme == ElementTheme.Dark ||
        (ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

    private static Color ThemeColor(string key, Color fallback) =>
        (Application.Current.Resources[key] as SolidColorBrush)?.Color ?? fallback;

    private void ApplyThemeBackground()
    {
        Background = new SolidColorBrush(ThemeColor("RuCanvasBrush", Color.FromArgb(255, 22, 24, 29)));
    }

    private void ApplyFrameTheme()
    {
        _frame.BorderBrush = new SolidColorBrush(ThemeColor("RuLineBrush", Color.FromArgb(255, 51, 65, 85)));
        var tickBrush = new SolidColorBrush(ThemeColor("RuAccentBrush", Color.FromArgb(255, 226, 232, 240)));
        foreach (var tick in _cornerTicks)
        {
            tick.Stroke = tickBrush;
        }
    }

    private void ApplyGiantTheme()
    {
        var echo = ThemeColor("RuAccentSoftBrush", Color.FromArgb(150, 40, 45, 55));
        _giantEcho.Foreground = new SolidColorBrush(echo);
        _giantEcho.Opacity = 0.55;
        var ink = ThemeColor("RuInkBrush", Color.FromArgb(255, 241, 245, 249));
        _giantGlyph.Foreground = new SolidColorBrush(ink);
        _giantGlyph.Opacity = 0.14;
    }

    private void ApplyProgressTheme()
    {
        _bootProgress.Background = new SolidColorBrush(ThemeColor("RuSurfaceBrush", Color.FromArgb(255, 32, 35, 42)));
        _bootProgress.Foreground = new SolidColorBrush(ThemeColor("RuAccentBrush", Color.FromArgb(255, 226, 232, 240)));
    }

    /// <summary>
    /// Updates startup status during loading phase.
    /// </summary>
    public void SetStatus(string text, bool isReady, bool isError = false)
    {
        if (_isDismissed) return;

        if (isReady || isError)
        {
            NotifyReady(text, isError);
        }
        else
        {
            _statusLabel.Text = text;
        }
    }

    /// <summary>
    /// Called when backend finishes loading: the overlay carries itself into
    /// the app with one exit transition instead of waiting for a button.
    /// </summary>
    public void NotifyReady(string? message = null, bool isError = false)
    {
        if (_isDismissed || _isReady) return;
        _isReady = true;

        _statusLabel.Text = string.IsNullOrWhiteSpace(message)
            ? (isError ? "Offline. Entering application..." : "Ready. Entering application...")
            : message;
        _bootProgress.Visibility = Visibility.Collapsed;

        _ = PlayExitAsync();
    }

    /// <summary>
    /// Dismisses the welcome screen, playing the exit transition first.
    /// </summary>
    public Task DismissAsync() => PlayExitAsync();

    private Task PlayExitAsync()
    {
        if (_isDismissed) return Task.CompletedTask;
        if (_exitStarted) return Task.CompletedTask;
        _exitStarted = true;
        _isDismissed = true;

        if (!RuApp.Services.UiTiming.AnimationsEnabled)
        {
            Visibility = Visibility.Collapsed;
            Dismissed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        var contentFade = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(450)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(contentFade, _content);
        Storyboard.SetTargetProperty(contentFade, "Opacity");

        var contentRise = new DoubleAnimation
        {
            From = 0.0,
            To = -28.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(550)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(contentRise, _contentTransform);
        Storyboard.SetTargetProperty(contentRise, "Y");

        var overlayFade = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(600)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(overlayFade, this);
        Storyboard.SetTargetProperty(overlayFade, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(contentFade);
        sb.Children.Add(contentRise);
        sb.Children.Add(overlayFade);

        var tcs = new TaskCompletionSource<bool>();
        sb.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            Dismissed?.Invoke(this, EventArgs.Empty);
            tcs.TrySetResult(true);
        };
        sb.Begin();

        return tcs.Task;
    }

    public void Dispose()
    {
    }
}
