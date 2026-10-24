using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace RuApp.Controls;

public sealed partial class WelcomeOverlay
{
    private readonly Grid _atmosphere = new() { IsHitTestVisible = false };
    private SpriteVisual? _ambientVisual;
    private CompositionRadialGradientBrush? _ambientBrush;

    private void InitializeAtmosphere()
    {
        AutomationProperties.SetAccessibilityView(_atmosphere, AccessibilityView.Raw);
        Children.Add(_atmosphere);
        _atmosphere.Loaded += OnAtmosphereLoaded;
        _atmosphere.Unloaded += OnAtmosphereUnloaded;
    }

    private void OnAtmosphereLoaded(object sender, RoutedEventArgs args)
    {
        if (_disposed || _isDismissed || _ambientVisual is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(_atmosphere).Compositor;
        _ambientBrush = compositor.CreateRadialGradientBrush();
        // A broad, stationary light below the frame leaves the logo's center quiet.
        _ambientBrush.EllipseCenter = new Vector2(0.5f, 1.08f);
        _ambientBrush.EllipseRadius = new Vector2(0.78f, 0.72f);
        _ambientBrush.ColorStops.Add(compositor.CreateColorGradientStop(0, default));
        _ambientBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.45f, default));
        _ambientBrush.ColorStops.Add(compositor.CreateColorGradientStop(1, default));
        _ambientVisual = compositor.CreateSpriteVisual();
        _ambientVisual.RelativeSizeAdjustment = Vector2.One;
        _ambientVisual.Brush = _ambientBrush;
        ElementCompositionPreview.SetElementChildVisual(_atmosphere, _ambientVisual);
        ApplyAtmosphereTheme();
    }

    private void ApplyAtmosphereTheme()
    {
        _atmosphere.Visibility = new AccessibilitySettings().HighContrast
            ? Visibility.Collapsed : Visibility.Visible;
        if (_ambientBrush is null) return;
        var green = ((SolidColorBrush)ThemeBrush("RuBrandGreenBrush")).Color;
        bool light = ActualTheme == ElementTheme.Light;
        _ambientBrush.ColorStops[0].Color = Color.FromArgb(light ? (byte)20 : (byte)42, green.R, green.G, green.B);
        _ambientBrush.ColorStops[1].Color = Color.FromArgb(light ? (byte)8 : (byte)16, green.R, green.G, green.B);
        _ambientBrush.ColorStops[2].Color = Color.FromArgb(0, green.R, green.G, green.B);
    }

    private void OnAtmosphereUnloaded(object sender, RoutedEventArgs args) => ReleaseAtmosphere();

    private void ReleaseAtmosphere()
    {
        ElementCompositionPreview.SetElementChildVisual(_atmosphere, null);
        _ambientVisual?.Dispose();
        _ambientBrush?.Dispose();
        _ambientVisual = null;
        _ambientBrush = null;
    }

    private void DisposeAtmosphere()
    {
        _atmosphere.Loaded -= OnAtmosphereLoaded;
        _atmosphere.Unloaded -= OnAtmosphereUnloaded;
        ReleaseAtmosphere();
    }
}
