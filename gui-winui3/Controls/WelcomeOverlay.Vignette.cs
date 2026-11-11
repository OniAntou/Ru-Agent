using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace RuApp.Controls;

public sealed partial class WelcomeOverlay
{
    private readonly Grid _vignette = new() { IsHitTestVisible = false };
    private SpriteVisual? _vignetteVisual;
    private CompositionRadialGradientBrush? _vignetteBrush;

    private void InitializeVignette()
    {
        AutomationProperties.SetAccessibilityView(_vignette, AccessibilityView.Raw);
        Children.Add(_vignette);
        _vignette.Loaded += OnVignetteLoaded;
        _vignette.Unloaded += (_, _) => ReleaseVignette();
    }

    private void OnVignetteLoaded(object sender, RoutedEventArgs args)
    {
        if (_disposed || _isDismissed || _vignetteVisual is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(_vignette).Compositor;
        _vignetteBrush = compositor.CreateRadialGradientBrush();
        // Dark corners, clear centre: it frames the mark without adding any light.
        _vignetteBrush.EllipseCenter = new Vector2(0.5f, 0.5f);
        _vignetteBrush.EllipseRadius = new Vector2(0.78f, 0.78f);
        _vignetteBrush.ColorStops.Add(compositor.CreateColorGradientStop(0, default));
        _vignetteBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.55f, default));
        _vignetteBrush.ColorStops.Add(compositor.CreateColorGradientStop(1, default));
        _vignetteVisual = compositor.CreateSpriteVisual();
        _vignetteVisual.RelativeSizeAdjustment = Vector2.One;
        _vignetteVisual.Brush = _vignetteBrush;
        ElementCompositionPreview.SetElementChildVisual(_vignette, _vignetteVisual);
        ApplyVignetteTheme();
    }

    private void ApplyVignetteTheme()
    {
        // Light has no darkness to frame, and high contrast takes no decoration.
        bool show = ActualTheme != ElementTheme.Light && !new AccessibilitySettings().HighContrast;
        _vignette.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (_vignetteBrush is null) return;
        _vignetteBrush.ColorStops[0].Color = Color.FromArgb(0, 0, 0, 0);
        _vignetteBrush.ColorStops[1].Color = Color.FromArgb(0, 0, 0, 0);
        _vignetteBrush.ColorStops[2].Color = Color.FromArgb(150, 0, 0, 0);
    }

    private void ReleaseVignette()
    {
        ElementCompositionPreview.SetElementChildVisual(_vignette, null);
        _vignetteVisual?.Dispose();
        _vignetteBrush?.Dispose();
        _vignetteVisual = null;
        _vignetteBrush = null;
    }

    private void DisposeVignette()
    {
        _vignette.Loaded -= OnVignetteLoaded;
        ReleaseVignette();
    }
}