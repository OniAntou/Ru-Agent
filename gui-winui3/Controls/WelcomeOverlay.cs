using System;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace RuApp.Controls;

/// <summary>
/// Native WinUI 3 1:1 pixel-level reproduction of BackgroundPaths hero screen.
/// Displays bold typography ("Ru Agent"), radiant sweeping Bézier streams,
/// and a high-end pill capsule button ("Discover Excellence →") revealed once ready.
/// </summary>
public sealed class WelcomeOverlay : Grid, IDisposable
{
    private readonly BackgroundPathsControl _backgroundPaths;
    private readonly StackPanel _titleContainer;
    private readonly TextBlock _titleText;
    private readonly TranslateTransform _titleTransform = new();

    private readonly Border _loadingBadge;
    private readonly TextBlock _statusLabel;
    private readonly Storyboard _loadingPulseStoryboard;

    private readonly Border _buttonContainer;
    private readonly TranslateTransform _buttonTransform = new();
    private readonly ScaleTransform _buttonScale = new();
    private readonly Button _enterButton;
    private readonly TextBlock _buttonLabel;
    private readonly TextBlock _arrowGlyph;
    private Storyboard? _arrowNudgeStoryboard;

    private bool _isDismissed;
    private bool _isReady;

    public event EventHandler? Dismissed;

    public bool IsDismissed => _isDismissed;
    public bool IsReady => _isReady;

    public WelcomeOverlay()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        ApplyThemeBackground();

        // 1. Background layer: 72 sweeping Bézier paths
        _backgroundPaths = new BackgroundPathsControl();
        Children.Add(_backgroundPaths);

        // 2. Center container matching React preview
        var centerContainer = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 800
        };

        // 3. Grand Title: "Ru Agent"
        _titleContainer = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderTransform = _titleTransform,
            Margin = new Thickness(0, 0, 0, 26),
            Opacity = 0.0
        };

        _titleText = new TextBlock
        {
            Text = "Ru Agent",
            FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
            FontSize = 90,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            CharacterSpacing = -45,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        ApplyTitleBrush();
        _titleContainer.Children.Add(_titleText);
        centerContainer.Children.Add(_titleContainer);

        // 4. Subtle Loading Status Pill (Active during backend boot)
        _loadingBadge = new Border
        {
            CornerRadius = new CornerRadius(18),
            Height = 36,
            Padding = new Thickness(20, 0, 20, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
            BorderThickness = new Thickness(1)
        };

        var loadingContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8
        };

        _statusLabel = new TextBlock
        {
            Text = "Starting system...",
            FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
            FontSize = 13.5,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center
        };

        loadingContent.Children.Add(_statusLabel);
        _loadingBadge.Child = loadingContent;

        // Subtle breathing animation during startup phase
        var pulseAnim = new DoubleAnimation
        {
            From = 0.50,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(850)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(pulseAnim, _loadingBadge);
        Storyboard.SetTargetProperty(pulseAnim, "Opacity");
        _loadingPulseStoryboard = new Storyboard();
        _loadingPulseStoryboard.Children.Add(pulseAnim);

        centerContainer.Children.Add(_loadingBadge);

        // 5. Pill Capsule Button: "Discover Excellence →" (Hidden initially, blossoms on ready)
        var buttonTransformGroup = new TransformGroup();
        _buttonScale.CenterX = 110;
        _buttonScale.CenterY = 24;
        buttonTransformGroup.Children.Add(_buttonScale);
        buttonTransformGroup.Children.Add(_buttonTransform);

        _buttonContainer = new Border
        {
            CornerRadius = new CornerRadius(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderTransform = buttonTransformGroup,
            Margin = new Thickness(0, 10, 0, 0),
            Opacity = 0.0,
            Visibility = Visibility.Collapsed
        };

        _enterButton = new Button
        {
            CornerRadius = new CornerRadius(24),
            Padding = new Thickness(32, 12, 32, 12),
            Height = 48,
            BorderThickness = new Thickness(1)
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_enterButton, "Discover Excellence");

        var buttonContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 10
        };

        _buttonLabel = new TextBlock
        {
            Text = "Discover Excellence",
            FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
            FontSize = 15.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

        _arrowGlyph = new TextBlock
        {
            Text = "→",
            FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

        buttonContent.Children.Add(_buttonLabel);
        buttonContent.Children.Add(_arrowGlyph);
        _enterButton.Content = buttonContent;
        _buttonContainer.Child = _enterButton;

        ApplyButtonTheme();

        _enterButton.Click += async (_, _) => await DismissAsync();
        _enterButton.PointerEntered += OnButtonPointerEntered;
        _enterButton.PointerExited += OnButtonPointerExited;

        centerContainer.Children.Add(_buttonContainer);

        Children.Add(centerContainer);

        Loaded += (_, _) =>
        {
            if (RuApp.Services.UiTiming.AnimationsEnabled)
            {
                _backgroundPaths.StartAnimations();
                _loadingPulseStoryboard.Begin();
                PlayTitleEntrance();
            }
            else
            {
                _titleContainer.Opacity = 1.0;
            }
        };

        Unloaded += (_, _) =>
        {
            _backgroundPaths.StopAnimations();
            _loadingPulseStoryboard.Stop();
            _arrowNudgeStoryboard?.Stop();
        };

        ActualThemeChanged += (_, _) =>
        {
            ApplyThemeBackground();
            ApplyTitleBrush();
            ApplyButtonTheme();
        };

        KeyDown += async (_, e) =>
        {
            if (_isReady && !_isDismissed && (e.Key == Windows.System.VirtualKey.Enter || e.Key == Windows.System.VirtualKey.Space))
            {
                await DismissAsync();
            }
        };

        _backgroundPaths.StartAnimations();
    }

    private void PlayTitleEntrance()
    {
        var titleFade = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(850)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(titleFade, _titleContainer);
        Storyboard.SetTargetProperty(titleFade, "Opacity");

        var titleSlide = new DoubleAnimation
        {
            From = 36.0,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(950)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(titleSlide, _titleTransform);
        Storyboard.SetTargetProperty(titleSlide, "Y");

        var sb = new Storyboard();
        sb.Children.Add(titleFade);
        sb.Children.Add(titleSlide);
        sb.Begin();
    }

    private bool IsDarkTheme() =>
        ActualTheme == ElementTheme.Dark ||
        (ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

    private static Color ThemeColor(string key, Color fallback) =>
        (Application.Current.Resources[key] as SolidColorBrush)?.Color ?? fallback;

    private void ApplyThemeBackground()
    {
        // Match the app canvas so the overlay melts into the GUI theme.
        Background = new SolidColorBrush(ThemeColor("RuCanvasBrush", Color.FromArgb(255, 22, 24, 29)));
    }

    private void ApplyTitleBrush()
    {
        Color ink = ThemeColor("RuInkBrush", Color.FromArgb(255, 241, 245, 249));
        Color muted = ThemeColor("RuMutedBrush", Color.FromArgb(255, 148, 163, 184));

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0.8, 1.0)
        };

        gradient.GradientStops.Add(new GradientStop { Color = ink, Offset = 0.0 });
        var tail = muted;
        tail.A = 225;
        gradient.GradientStops.Add(new GradientStop { Color = tail, Offset = 1.0 });

        _titleText.Foreground = gradient;
    }

    private void ApplyButtonTheme()
    {
        bool isDark = IsDarkTheme();
        Color surface = ThemeColor("RuSurfaceBrush", Color.FromArgb(255, 32, 35, 42));
        Color line = ThemeColor("RuLineBrush", Color.FromArgb(255, 51, 65, 85));
        Color muted = ThemeColor("RuMutedBrush", Color.FromArgb(255, 148, 163, 184));
        Color accent = ThemeColor("RuAccentBrush", Color.FromArgb(255, 226, 232, 240));
        Color canvas = ThemeColor("RuCanvasBrush", Color.FromArgb(255, 22, 24, 29));

        _loadingBadge.Background = new SolidColorBrush(Color.FromArgb(isDark ? (byte)170 : (byte)180, surface.R, surface.G, surface.B));
        _loadingBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(40, line.R, line.G, line.B));
        _statusLabel.Foreground = new SolidColorBrush(Color.FromArgb(210, muted.R, muted.G, muted.B));

        _enterButton.Background = new SolidColorBrush(accent);
        _enterButton.Foreground = isDark
            ? new SolidColorBrush(canvas)
            : new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        _enterButton.BorderBrush = new SolidColorBrush(Color.FromArgb(45, line.R, line.G, line.B));
    }

    private void OnButtonPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _arrowNudgeStoryboard?.Stop();
        _arrowGlyph.Translation = new System.Numerics.Vector3(4, 0, 0);
        _arrowGlyph.Opacity = 1.0;

        bool isDark = IsDarkTheme();

        Color soft = ThemeColor("RuAccentSoftBrush", Color.FromArgb(255, 40, 45, 55));
        Color line = ThemeColor("RuLineBrush", Color.FromArgb(255, 51, 65, 85));
        Color ink = ThemeColor("RuInkBrush", Color.FromArgb(255, 241, 245, 249));
        Color canvas = ThemeColor("RuCanvasBrush", Color.FromArgb(255, 22, 24, 29));
        _enterButton.Background = new SolidColorBrush(soft);
        _enterButton.Foreground = new SolidColorBrush(isDark ? canvas : ink);
        _enterButton.BorderBrush = new SolidColorBrush(Color.FromArgb(isDark ? (byte)80 : (byte)70, line.R, line.G, line.B));
    }

    private void OnButtonPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _arrowGlyph.Translation = new System.Numerics.Vector3(0, 0, 0);
        _arrowGlyph.Opacity = 0.85;
        ApplyButtonTheme();
        _arrowNudgeStoryboard?.Begin();
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
    /// Called when backend finishes loading: blossoms the pill button into "Discover Excellence →".
    /// </summary>
    public void NotifyReady(string? message = null, bool isError = false)
    {
        if (_isDismissed || _isReady) return;
        _isReady = true;

        _loadingPulseStoryboard.Stop();

        // 1. Fade out the loading badge smoothly
        var fadeOutLoading = new DoubleAnimation
        {
            From = _loadingBadge.Opacity,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(fadeOutLoading, _loadingBadge);
        Storyboard.SetTargetProperty(fadeOutLoading, "Opacity");

        var sbLoading = new Storyboard();
        sbLoading.Children.Add(fadeOutLoading);
        sbLoading.Completed += (_, _) =>
        {
            _loadingBadge.Visibility = Visibility.Collapsed;

            // 2. Prepare button
            _buttonLabel.Text = isError ? "Enter Application (Offline)" : "Discover Excellence";
            _buttonContainer.Visibility = Visibility.Visible;
            _buttonContainer.Opacity = 0.0;
            _buttonScale.ScaleX = 0.82;
            _buttonScale.ScaleY = 0.82;
            _buttonTransform.Y = 14.0;

            if (!RuApp.Services.UiTiming.AnimationsEnabled)
            {
                _buttonContainer.Opacity = 1.0;
                _buttonScale.ScaleX = 1.0;
                _buttonScale.ScaleY = 1.0;
                _buttonTransform.Y = 0.0;
                _enterButton.Focus(FocusState.Programmatic);
                return;
            }

            // 3. Blossom spring entrance animation
            var buttonFade = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(350)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(buttonFade, _buttonContainer);
            Storyboard.SetTargetProperty(buttonFade, "Opacity");

            var buttonSlide = new DoubleAnimation
            {
                From = 14.0,
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(500)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(buttonSlide, _buttonTransform);
            Storyboard.SetTargetProperty(buttonSlide, "Y");

            var buttonScaleX = new DoubleAnimation
            {
                From = 0.82,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(550)),
                EasingFunction = new BackEase { Amplitude = 0.30, EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(buttonScaleX, _buttonScale);
            Storyboard.SetTargetProperty(buttonScaleX, "ScaleX");

            var buttonScaleY = new DoubleAnimation
            {
                From = 0.82,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(550)),
                EasingFunction = new BackEase { Amplitude = 0.30, EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(buttonScaleY, _buttonScale);
            Storyboard.SetTargetProperty(buttonScaleY, "ScaleY");

            var sbButton = new Storyboard();
            sbButton.Children.Add(buttonFade);
            sbButton.Children.Add(buttonSlide);
            sbButton.Children.Add(buttonScaleX);
            sbButton.Children.Add(buttonScaleY);
            sbButton.Completed += (_, _) =>
            {
                _enterButton.Focus(FocusState.Programmatic);
                StartArrowNudge();
            };
            sbButton.Begin();
        };
        sbLoading.Begin();
    }

    private void StartArrowNudge()
    {
        if (_isDismissed) return;
        if (!RuApp.Services.UiTiming.AnimationsEnabled) return;

        var nudgeAnim = new DoubleAnimation
        {
            From = 0.70,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(850)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(nudgeAnim, _arrowGlyph);
        Storyboard.SetTargetProperty(nudgeAnim, "Opacity");

        _arrowNudgeStoryboard = new Storyboard();
        _arrowNudgeStoryboard.Children.Add(nudgeAnim);
        _arrowNudgeStoryboard.Begin();
    }

    /// <summary>
    /// Dismisses the welcome screen with a smooth fade-out.
    /// </summary>
    public async Task DismissAsync()
    {
        if (_isDismissed) return;
        _isDismissed = true;

        if (!RuApp.Services.UiTiming.AnimationsEnabled)
        {
            Visibility = Visibility.Collapsed;
            _backgroundPaths.Dispose();
            _loadingPulseStoryboard.Stop();
            _arrowNudgeStoryboard?.Stop();
            Dismissed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var fadeOut = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(420)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(fadeOut, this);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(fadeOut);

        var tcs = new TaskCompletionSource<bool>();
        sb.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            _backgroundPaths.Dispose();
            _loadingPulseStoryboard.Stop();
            _arrowNudgeStoryboard?.Stop();
            Dismissed?.Invoke(this, EventArgs.Empty);
            tcs.TrySetResult(true);
        };
        sb.Begin();

        await tcs.Task;
    }

    public void Dispose()
    {
        _backgroundPaths.Dispose();
        _loadingPulseStoryboard.Stop();
        _arrowNudgeStoryboard?.Stop();
    }
}
