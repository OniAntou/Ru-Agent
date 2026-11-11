using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RuApp.Services;

namespace RuApp.Controls;

/// <summary>
/// GUI startup identity: native Inter wordmark with a colour wave, over the meteor shower.
/// Startup never waits for the animation.
/// </summary>
public sealed partial class WelcomeOverlay : Grid, IDisposable
{
    private readonly StackPanel _content;
    private readonly Viewbox _brandHost;
    private readonly TextBlock _wordmark;
    private readonly TextBlock _statusLabel;
    private readonly TextBlock _slowHint;
    private readonly Button _continueButton;
    private readonly TranslateTransform _brandSlide = new();
    private bool _isDismissed;
    private bool _isReady;
    private bool _hasError;
    private bool _disposed;

    public event EventHandler? Dismissed;
    public bool IsDismissed => _isDismissed;
    public bool IsReady => _isReady;

    public WelcomeOverlay()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        AutomationProperties.SetName(this, "Ru Agent startup");
        InitializeBackdrop();
        var font = (FontFamily)Application.Current.Resources["RuUiFontFamily"];
        _wordmark = BuildWordmark(font);
        var wordmark = new StackPanel { Orientation = Orientation.Horizontal };
        wordmark.Children.Add(_wordmark);        _brandHost = new Viewbox
        {
            Child = wordmark, Width = 340, Height = 76,
            Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 18), RenderTransform = _brandSlide,
        };
        _statusLabel = new TextBlock
        {
            Text = "Getting Ru ready…", FontFamily = font, FontSize = 13,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            LineHeight = 20, MinHeight = 20,
        };
        AutomationProperties.SetLiveSetting(_statusLabel, AutomationLiveSetting.Polite);
        _slowHint = new TextBlock
        {
            Text = "Still starting. This is taking longer than usual.", FontFamily = font, FontSize = 12,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed,
        };
        _continueButton = new Button
        {
            Content = "Open app", FontFamily = font, FontSize = 13,
            Padding = new Thickness(18, 8, 18, 8), MinHeight = 36,
            Margin = new Thickness(0, 24, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(_continueButton, "Open app");
        _continueButton.Click += (_, _) => _ = PlayExitAsync();
        // ScrollViewer aligns the panel's bounds; a stretched StackPanel still
        // stacks its children at the top of those bounds. Center the panel itself.
        _content = new StackPanel
        {
            MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _content.Children.Add(_brandHost);
        _content.Children.Add(_statusLabel);
        _content.Children.Add(_slowHint);
        _content.Children.Add(_continueButton);
        var contentScroller = new ScrollViewer
        {
            Content = _content, Padding = new Thickness(24),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Children.Add(contentScroller);
        AttachCamera(contentScroller);
        ApplyTheme();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        ActualThemeChanged += (_, _) => ApplyTheme();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        KeyDown += OnOverlayKeyDown;
    }

    private static Brush ThemeBrush(string key) => (Brush)Application.Current.Resources[key];

    private void ApplyTheme()
    {
        Background = ThemeBrush("RuCanvasBrush");
        ApplyBackdropTheme();
        ApplyWordmarkResting();
        _statusLabel.Foreground = ThemeBrush(_hasError ? "RuDangerBrush" : "RuMutedBrush");
        _slowHint.Foreground = ThemeBrush("RuMutedBrush");
        _continueButton.Foreground = ThemeBrush("RuInkBrush");
        _continueButton.Background = ThemeBrush("RuSurfaceBrush");
        _continueButton.BorderBrush = ThemeBrush("RuLineBrush");
    }

    private void SetStatusText(string text)
    {
        // The real state stays readable immediately, even during the entrance.
        _statusLabel.Text = text;
        AutomationProperties.SetName(_statusLabel, text);
    }

    public void SetStatus(string text, bool isReady, bool isError = false)
    {
        if (_disposed || _isDismissed) return;
        if (isReady || isError) NotifyReady(text, isError);
        else SetStatusText(text);
    }

    public void NotifyReady(string? message = null, bool isError = false)
    {
        if (_disposed || _isDismissed || _isReady) return;
        _isReady = true;
        _hasError = isError;
        _watchTimer?.Stop();
        _clock.Stop();
        _slowHint.Visibility = Visibility.Collapsed;
        SetStatusText(message ?? (isError ? "Ru couldn't finish starting." : "Ready"));
        ApplyTheme();
        if (isError)
        {
            StopBrandMotion(settle: true);
            _continueButton.Visibility = Visibility.Visible;
            if (IsLoaded) _continueButton.Focus(FocusState.Programmatic);
            return;
        }
        _ = PlayExitAsync();
    }

    public Task DismissAsync() => PlayExitAsync();

    private void OnOverlayKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!_isReady || _isDismissed) return;
        if (args.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
        {
            _ = PlayExitAsync();
            args.Handled = true;
        }
    }
}
