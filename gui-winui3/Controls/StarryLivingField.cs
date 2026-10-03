using System;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace RuApp.Controls;

/// <summary>
/// Living Starry Night: twinkling glows pinned to the painting's real stars and
/// moon, plus slow orbiting glows around its two vortexes. Coordinates are
/// normalized to the StarryNight.jpg asset (1280x1014) and cover-fit mapped, so
/// every glow sits on the feature Van Gogh painted.
/// </summary>
public sealed class StarryLivingField : Canvas, IDisposable
{
    private const double TickClampSeconds = 0.10;
    private const double AssetWidth = 1280.0;
    private const double AssetHeight = 1014.0;

    // (x, y, glowSize, baseOpacity): the moon plus the painting's bright stars.
    private static readonly (double X, double Y, float Size, double Opacity)[] StarLights =
    [
        (0.872, 0.195, 120f, 0.55),
        (0.068, 0.058, 64f, 0.65),
        (0.192, 0.024, 44f, 0.55),
        (0.283, 0.052, 52f, 0.60),
        (0.378, 0.078, 40f, 0.50),
        (0.190, 0.190, 56f, 0.65),
        (0.335, 0.348, 36f, 0.50),
        (0.283, 0.548, 60f, 0.60),
        (0.655, 0.258, 44f, 0.55),
        (0.032, 0.468, 32f, 0.45),
        (0.118, 0.498, 40f, 0.50),
    ];

    // (centerY, width, height, driftSpeed, baseOpacity): broad translucent bands
    // sweeping left across the sky, like the paint flowing.
    private static readonly (double Y, float W, float H, double Speed, double Opacity)[] Flows =
    [
        (0.150, 520f, 110f, 0.016, 0.34),
        (0.270, 640f, 130f, 0.011, 0.30),
        (0.390, 560f, 120f, 0.019, 0.36),
        (0.500, 700f, 140f, 0.008, 0.26),
        (0.080, 420f, 90f, 0.023, 0.30),
    ];

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private ContainerVisual? _rootVisual;
    private ContainerVisual? _glowContainer;
    private TwinkleVisual[]? _lights;
    private FlowBandVisual[]? _flows;
    private MoonHaloVisual? _moonHalo;

    private bool _isLoaded;
    private bool _isActive;
    private bool _disposed;
    private double _lastClockSeconds;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_disposed || _isActive == value) return;
            _isActive = value;
            SyncActivity();
        }
    }

    public StarryLivingField()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        Clip = new RectangleGeometry();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        ActualThemeChanged += OnActualThemeChanged;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        RebuildCompositionTree();
    }

    private void RebuildCompositionTree()
    {
        StopAndClear();

        if (_rootVisual is not null)
        {
            ElementCompositionPreview.SetElementChildVisual(this, null);
            _rootVisual.Dispose();
            _rootVisual = null;
            _lights = null;
            _flows = null;
            _moonHalo = null;
        }

        if (_isActive && _isLoaded && !_disposed)
        {
            SyncActivity();
        }
    }

    private void EnsureCompositionTree()
    {
        if (_rootVisual is not null) return;

        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _rootVisual = compositor.CreateContainerVisual();
        _glowContainer = compositor.CreateContainerVisual();

        Color gold = Color.FromArgb(255, 232, 200, 110);
        Color pale = Color.FromArgb(255, 243, 237, 220);

        if (ActualWidth > 0 && ActualHeight > 0)
        {
            _rootVisual.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
        }

        _rootVisual.Children.InsertAtTop(_glowContainer);

        _lights = new TwinkleVisual[StarLights.Length];
        for (var i = 0; i < _lights.Length; i++)
        {
            var (x, y, size, opacity) = StarLights[i];
            _lights[i] = new TwinkleVisual(compositor, gold, pale, x, y, size, opacity);
            _glowContainer.Children.InsertAtTop(_lights[i].Layer);
        }

        _moonHalo = new MoonHaloVisual(compositor, gold, pale);
        _glowContainer.Children.InsertAtBottom(_moonHalo.Layer);

        _flows = new FlowBandVisual[Flows.Length];
        for (var i = 0; i < _flows.Length; i++)
        {
            var (y, w, h, speed, opacity) = Flows[i];
            _flows[i] = new FlowBandVisual(compositor, pale, gold, y, w, h, speed, opacity, i);
            _glowContainer.Children.InsertAtBottom(_flows[i].Layer);
        }

        ElementCompositionPreview.SetElementChildVisual(this, _rootVisual);
    }

    /// <summary>
    /// Cover-fit mapping: the painting is UniformToFill, so this mirrors the
    /// Image layout to land each glow on its painted feature.
    /// </summary>
    private (double Scale, double OffsetX, double OffsetY) CoverFit()
    {
        double scale = Math.Max(ActualWidth / AssetWidth, ActualHeight / AssetHeight);
        double offsetX = (ActualWidth - (AssetWidth * scale)) / 2.0;
        double offsetY = (ActualHeight - (AssetHeight * scale)) / 2.0;
        return (scale, offsetX, offsetY);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        EnsureCompositionTree();
        RefreshClip();
        SyncActivity();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        StopAndClear();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RefreshClip();
        if (_rootVisual is not null && ActualWidth > 0 && ActualHeight > 0)
        {
            _rootVisual.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
        }
    }

    private void SyncActivity()
    {
        if (_disposed || !_isLoaded || !_isActive)
        {
            StopAndClear();
            return;
        }

        if (!RuApp.Services.UiTiming.AnimationsEnabled)
        {
            StopAndClear();
            return;
        }

        EnsureCompositionTree();
        Visibility = Visibility.Visible;
        _clock.Restart();
        _lastClockSeconds = 0;
        _timer.Start();
    }

    private void OnTimerTick(object? sender, object e)
    {
        if (_lights is null || _flows is null || _moonHalo is null
            || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        double now = _clock.Elapsed.TotalSeconds;
        double delta = Math.Min(now - _lastClockSeconds, TickClampSeconds);
        _lastClockSeconds = now;

        var (scale, offsetX, offsetY) = CoverFit();
        foreach (var light in _lights)
        {
            light.Advance(now, scale, offsetX, offsetY);
        }
        foreach (var flow in _flows)
        {
            flow.Advance(delta, now, scale, offsetX, offsetY);
        }
        _moonHalo.Advance(now, scale, offsetX, offsetY);
    }

    private void RefreshClip()
    {
        Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, ActualWidth, ActualHeight)
        };
    }

    private void StopAndClear()
    {
        _timer.Stop();
        _clock.Stop();
        _lastClockSeconds = 0;
        if (_lights is not null)
        {
            foreach (var light in _lights)
            {
                light.Hide();
            }
        }
        if (_flows is not null)
        {
            foreach (var flow in _flows)
            {
                flow.Hide();
            }
        }
        _moonHalo?.Hide();
        Visibility = Visibility.Collapsed;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _isActive = false;
        StopAndClear();
        _timer.Tick -= OnTimerTick;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        SizeChanged -= OnSizeChanged;
        ActualThemeChanged -= OnActualThemeChanged;

        if (_rootVisual is not null)
        {
            ElementCompositionPreview.SetElementChildVisual(this, null);
            _rootVisual.Dispose();
            _rootVisual = null;
        }
    }

    private sealed class TwinkleVisual
    {
        public ContainerVisual Layer { get; }
        private readonly SpriteVisual _visual;
        private readonly double _x;
        private readonly double _y;
        private readonly float _size;
        private readonly double _baseOpacity;
        private readonly double _speed;
        private readonly double _phase;

        public TwinkleVisual(Compositor compositor, Color gold, Color pale,
            double x, double y, float size, double opacity)
        {
            Layer = compositor.CreateContainerVisual();
            _visual = compositor.CreateSpriteVisual();

            var brush = compositor.CreateRadialGradientBrush();
            brush.EllipseCenter = new Vector2(0.5f, 0.5f);
            brush.EllipseRadius = new Vector2(0.5f, 0.5f);
            brush.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(235, pale.R, pale.G, pale.B)));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(0.35f, Color.FromArgb(150, gold.R, gold.G, gold.B)));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, gold.R, gold.G, gold.B)));
            _visual.Brush = brush;

            _x = x;
            _y = y;
            _size = size;
            _baseOpacity = opacity;
            _speed = 1.0 + (Random.Shared.NextDouble() * 1.8);
            _phase = Random.Shared.NextDouble() * Math.PI * 2;

            _visual.Size = new Vector2(_size, _size);
            Layer.Children.InsertAtTop(_visual);
        }

        public void Advance(double nowSeconds, double scale, double offsetX, double offsetY)
        {
            double twinkle = 0.65 + (0.35 * (0.5 + (0.5 * Math.Sin((nowSeconds * _speed) + _phase))));

            _visual.Offset = new Vector3(
                (float)(((_x * AssetWidth) * scale) + offsetX) - (_size / 2f),
                (float)(((_y * AssetHeight) * scale) + offsetY) - (_size / 2f),
                0f);
            _visual.Opacity = (float)(_baseOpacity * twinkle);
            Layer.IsVisible = true;
        }

        public void Hide()
        {
            Layer.IsVisible = false;
        }
    }

    /// <summary>
    /// One broad streak of sky drifting left and wrapping around, the visible
    /// current of the painting. Wide soft ellipse, clearly noticeable.
    /// </summary>
    private sealed class FlowBandVisual
    {
        public ContainerVisual Layer { get; }
        private readonly SpriteVisual _visual;
        private double _x;
        private readonly double _y;
        private readonly double _speed;
        private readonly double _baseOpacity;
        private readonly float _w;
        private readonly float _h;

        public FlowBandVisual(Compositor compositor, Color pale, Color gold,
            double y, float w, float h, double speed, double opacity, int index)
        {
            Layer = compositor.CreateContainerVisual();
            _visual = compositor.CreateSpriteVisual();

            var brush = compositor.CreateRadialGradientBrush();
            brush.EllipseCenter = new Vector2(0.5f, 0.5f);
            brush.EllipseRadius = new Vector2(0.5f, 0.32f);
            brush.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(110, pale.R, pale.G, pale.B)));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(0.55f, Color.FromArgb(55, gold.R, gold.G, gold.B)));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, gold.R, gold.G, gold.B)));
            _visual.Brush = brush;

            _x = ((index * 0.23) % 1.2) - 0.1;
            _y = y;
            _speed = speed;
            _baseOpacity = opacity;
            _w = w;
            _h = h;

            _visual.Size = new Vector2(_w, _h);
            Layer.Children.InsertAtTop(_visual);
        }

        public void Advance(double deltaSeconds, double nowSeconds,
            double scale, double offsetX, double offsetY)
        {
            _x -= _speed * deltaSeconds;
            if (_x < -0.45) _x = 1.15;

            double breathe = 0.8 + (0.2 * Math.Sin(nowSeconds * 0.5 + _x * 9.0));

            _visual.Offset = new Vector3(
                (float)(((_x * AssetWidth) * scale) + offsetX) - (_w / 2f),
                (float)(((_y * AssetHeight) * scale) + offsetY) - (_h / 2f),
                0f);
            _visual.Opacity = (float)(_baseOpacity * breathe);
            Layer.IsVisible = true;
        }

        public void Hide()
        {
            Layer.IsVisible = false;
        }
    }

    /// <summary>
    /// The moon's breathing halo, pinned to the painted crescent.
    /// </summary>
    private sealed class MoonHaloVisual
    {
        public ContainerVisual Layer { get; }
        private readonly SpriteVisual _visual;
        private const double MoonX = 0.872;
        private const double MoonY = 0.195;
        private const float BaseSize = 190f;

        public MoonHaloVisual(Compositor compositor, Color gold, Color pale)
        {
            Layer = compositor.CreateContainerVisual();
            _visual = compositor.CreateSpriteVisual();

            var brush = compositor.CreateRadialGradientBrush();
            brush.EllipseCenter = new Vector2(0.5f, 0.5f);
            brush.EllipseRadius = new Vector2(0.5f, 0.5f);
            brush.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(60, pale.R, pale.G, pale.B)));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(0.45f, Color.FromArgb(90, gold.R, gold.G, gold.B)));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, gold.R, gold.G, gold.B)));
            _visual.Brush = brush;

            _visual.Size = new Vector2(BaseSize, BaseSize);
            Layer.Children.InsertAtTop(_visual);
        }

        public void Advance(double nowSeconds, double scale, double offsetX, double offsetY)
        {
            double breathe = 0.5 + (0.5 * Math.Sin(nowSeconds * (Math.PI * 2 / 6.0)));
            float size = BaseSize * (float)(0.94 + (0.10 * breathe));

            _visual.Size = new Vector2(size, size);
            _visual.Offset = new Vector3(
                (float)(((MoonX * AssetWidth) * scale) + offsetX) - (size / 2f),
                (float)(((MoonY * AssetHeight) * scale) + offsetY) - (size / 2f),
                0f);
            _visual.Opacity = (float)(0.55 + (0.35 * breathe));
            Layer.IsVisible = true;
        }

        public void Hide()
        {
            Layer.IsVisible = false;
        }
    }
}
