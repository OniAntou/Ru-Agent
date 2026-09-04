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
/// Displays two-line bold typography ("Background Paths"), radiant sweeping Bézier streams,
/// and a high-end pill capsule button ("Discover Excellence →") revealed once ready.
/// </summary>
public sealed class WelcomeOverlay : Grid, IDisposable
{
    private readonly BackgroundPathsControl _backgroundPaths;
    private readonly StackPanel _titleContainer;
    private readonly TextBlock _titleWord1;
    private readonly TextBlock _titleWord2;
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

        // 3. Two-Line Grand Title: "Background" / "Paths"
        _titleContainer = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderTransform = _titleTransform,
            Margin = new Thickness(0, 0, 0, 26),
            Opacity = 0.0
        };

        _titleWord1 = new TextBlock
        {
            Text = "Background",
            FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
            FontSize = 88,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            CharacterSpacing = -45,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        _titleWord2 = new TextBlock
        {
            Text = "Paths",
            FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
            FontSize = 88,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            CharacterSpacing = -45,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, -16, 0, 0)
        };

        ApplyTitleBrush();
        _titleContainer.Children.Add(_titleWord1);
        _titleContainer.Children.Add(_titleWord2);
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
            Text = "Đang khởi động hệ thống...",
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
            _backgroundPaths.StartAnimations();
            _loadingPulseStoryboard.Begin();
            PlayTitleEntrance();
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

    private void ApplyThemeBackground()
    {
        bool isDark = ActualTheme == ElementTheme.Dark ||
            (ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        // Pure pitch obsidian black matching preview image
        Background = isDark
            ? new SolidColorBrush(Color.FromArgb(255, 10, 10, 10))
            : new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
    }

    private void ApplyTitleBrush()
    {
        bool isDark = ActualTheme == ElementTheme.Dark ||
            (ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0.8, 1.0)
        };

        if (isDark)
        {
            gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 255, 255, 255), Offset = 0.0 });
            gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(225, 212, 212, 216), Offset = 1.0 });
        }
        else
        {
            gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 15, 23, 42), Offset = 0.0 });
            gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(220, 71, 85, 105), Offset = 1.0 });
        }

        _titleWord1.Foreground = gradient;
        _titleWord2.Foreground = gradient;
    }

    private void ApplyButtonTheme()
    {
        bool isDark = ActualTheme == ElementTheme.Dark ||
            (ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        if (isDark)
        {
            _loadingBadge.Background = new SolidColorBrush(Color.FromArgb(170, 20, 20, 24));
            _loadingBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            _statusLabel.Foreground = new SolidColorBrush(Color.FromArgb(210, 161, 161, 170));

            _enterButton.Background = new SolidColorBrush(Color.FromArgb(235, 10, 10, 12));
            _enterButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            _enterButton.BorderBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255));
        }
        else
        {
            _loadingBadge.Background = new SolidColorBrush(Color.FromArgb(180, 244, 244, 245));
            _loadingBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
            _statusLabel.Foreground = new SolidColorBrush(Color.FromArgb(210, 113, 113, 122));

            _enterButton.Background = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255));
            _enterButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 15, 23, 42));
            _enterButton.BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
        }
    }

    private void OnButtonPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _arrowNudgeStoryboard?.Stop();
        _arrowGlyph.Translation = new System.Numerics.Vector3(4, 0, 0);
        _arrowGlyph.Opacity = 1.0;

        bool isDark = ActualTheme == ElementTheme.Dark ||
            (ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        _enterButton.Background = isDark
            ? new SolidColorBrush(Color.FromArgb(255, 22, 22, 26))
            : new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        _enterButton.BorderBrush = isDark
            ? new SolidColorBrush(Color.FromArgb(80, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
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
            _buttonLabel.Text = isError ? "Vào ứng dụng (Offline)" : "Discover Excellence";
            _buttonContainer.Visibility = Visibility.Visible;
            _buttonContainer.Opacity = 0.0;
            _buttonScale.ScaleX = 0.82;
            _buttonScale.ScaleY = 0.82;
            _buttonTransform.Y = 14.0;

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
