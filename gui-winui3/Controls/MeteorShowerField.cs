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

public sealed class MeteorShowerField : Canvas, IDisposable
{
    private const int MaxMeteorCount = 8;
    private const double LeadInSeconds = 0.4;
    private const double ShootingStarCycleSeconds = 4.5;
    private const double TickClampSeconds = 0.10;

    private static readonly StarSpec[] Stars =
    {
        // === TIER 1: MAJOR JEWEL STARS (Prominent, placed in open sky & outer corners) ===
        new(0.08, 0.12, 3.8f, 1.4, 0.00, 0.70f, true),
        new(0.92, 0.10, 4.2f, 1.6, 1.20, 0.75f, true),
        new(0.05, 0.45, 3.6f, 1.8, 2.40, 0.65f, true),
        new(0.94, 0.40, 4.0f, 1.3, 0.80, 0.72f, true),
        new(0.12, 0.75, 3.5f, 1.7, 1.90, 0.68f, true),
        new(0.88, 0.72, 4.4f, 1.5, 3.10, 0.78f, true),
        new(0.72, 0.18, 3.6f, 1.9, 0.50, 0.65f, true),
        new(0.26, 0.08, 3.8f, 1.4, 2.20, 0.70f, true),

        // === TIER 2: MEDIUM SHINING STARS (Soft halos) ===
        new(0.03, 0.06, 2.2f, 2.1, 0.40, 0.50f),
        new(0.18, 0.05, 2.0f, 2.6, 2.10, 0.45f),
        new(0.38, 0.06, 2.4f, 1.8, 1.50, 0.52f),
        new(0.60, 0.05, 1.8f, 2.8, 0.70, 0.42f),
        new(0.82, 0.06, 2.2f, 2.2, 2.80, 0.48f),
        new(0.96, 0.18, 2.0f, 2.5, 1.10, 0.45f),

        new(0.02, 0.28, 2.0f, 2.4, 0.90, 0.46f),
        new(0.14, 0.32, 2.2f, 2.0, 2.60, 0.48f),
        new(0.86, 0.26, 2.4f, 1.7, 1.30, 0.52f),
        new(0.97, 0.32, 1.8f, 2.7, 0.20, 0.40f),

        new(0.03, 0.60, 2.2f, 2.1, 1.70, 0.48f),
        new(0.15, 0.62, 2.0f, 2.5, 3.20, 0.44f),
        new(0.84, 0.54, 2.4f, 1.8, 0.60, 0.50f),
        new(0.96, 0.62, 2.0f, 2.3, 2.00, 0.46f),

        new(0.06, 0.86, 2.4f, 1.6, 1.40, 0.52f),
        new(0.20, 0.82, 2.0f, 2.4, 2.90, 0.45f),
        new(0.80, 0.84, 2.2f, 2.0, 0.30, 0.48f),
        new(0.94, 0.86, 2.0f, 2.6, 1.80, 0.44f),

        // === TIER 3: DISTANT COSMIC DUST (Subtle depth points) ===
        new(0.05, 0.02, 1.2f, 3.2, 0.80, 0.30f),
        new(0.12, 0.04, 1.0f, 3.5, 2.30, 0.25f),
        new(0.30, 0.03, 1.4f, 2.9, 1.10, 0.32f),
        new(0.48, 0.03, 1.1f, 3.8, 3.50, 0.28f),
        new(0.68, 0.04, 1.3f, 3.1, 0.40, 0.30f),
        new(0.76, 0.02, 1.0f, 3.6, 2.00, 0.25f),
        new(0.88, 0.03, 1.2f, 3.3, 1.60, 0.32f),

        new(0.02, 0.18, 1.1f, 3.4, 2.70, 0.26f),
        new(0.08, 0.24, 1.3f, 3.0, 0.50, 0.30f),
        new(0.90, 0.20, 1.0f, 3.9, 1.90, 0.22f),
        new(0.98, 0.24, 1.4f, 2.7, 0.70, 0.32f),

        new(0.02, 0.38, 1.2f, 3.3, 1.20, 0.28f),
        new(0.10, 0.48, 1.0f, 3.8, 2.80, 0.24f),
        new(0.88, 0.44, 1.3f, 2.9, 0.30, 0.30f),
        new(0.96, 0.48, 1.1f, 3.5, 1.70, 0.26f),

        new(0.04, 0.68, 1.2f, 3.1, 2.20, 0.28f),
        new(0.16, 0.72, 1.0f, 3.9, 0.60, 0.22f),
        new(0.82, 0.68, 1.3f, 3.0, 1.80, 0.30f),
        new(0.92, 0.76, 1.1f, 3.4, 3.00, 0.26f),

        new(0.04, 0.92, 1.4f, 2.7, 0.40, 0.34f),
        new(0.16, 0.90, 1.0f, 3.7, 2.50, 0.24f),
        new(0.84, 0.92, 1.2f, 3.2, 1.10, 0.28f),
        new(0.96, 0.92, 1.4f, 2.9, 2.90, 0.32f)
    };

    // Base trajectory archetypes used for calibration
    private static readonly MeteorSpec[] Burst =
    {
        new(0.00, -0.20, -0.05, 1.25, 0.45, 1.40, 0.95f, 0.48f, 360f),
        new(0.00,  0.15, -0.20, 1.20, 0.95, 1.35, 1.00f, 0.52f, 380f),
        new(0.00, -0.20, 0.40, 1.20, 1.10, 1.50, 0.90f, 0.42f, 320f),
        new(0.00, -0.25, 0.15, 1.25, 0.75, 1.45, 1.05f, 0.55f, 420f),
        new(0.00,  0.35, -0.20, 0.95, 1.25, 1.30, 0.95f, 0.46f, 340f),
        new(0.00,  0.40, -0.15, 1.30, 0.35, 1.25, 0.85f, 0.44f, 300f),
        new(0.00, -0.20, 0.65, 1.25, 1.15, 1.55, 0.95f, 0.45f, 350f),
        new(0.00, -0.15, -0.10, 1.30, 0.60, 1.40, 1.00f, 0.50f, 400f)
    };

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private ContainerVisual? _rootVisual;
    private ContainerVisual? _starContainer;
    private ContainerVisual? _meteorContainer;
    private SpriteVisual? _nebulaGlow;
    private StarVisual[]? _starVisuals;
    private MeteorVisual[]? _meteorVisuals;

    private bool _isLoaded;
    private bool _isActive;
    private bool _disposed;
    private Phase _phase = Phase.Idle;
    private double _phaseElapsed;
    private double _lastClockSeconds;
    private double _currentRestDuration = 1.6;
    private int _meteorPoolIndex;

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

    public MeteorShowerField()
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
            _starVisuals = null;
            _meteorVisuals = null;
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
        _starContainer = compositor.CreateContainerVisual();
        _meteorContainer = compositor.CreateContainerVisual();

        var (accentColor, softColor, starCore, starCorona, isDark) = GetThemeColors();

        // Subtle ambient celestial nebula / mineral paper glaze
        _nebulaGlow = compositor.CreateSpriteVisual();
        var nebulaBrush = compositor.CreateLinearGradientBrush();
        nebulaBrush.StartPoint = new Vector2(0.1f, 0.1f);
        nebulaBrush.EndPoint = new Vector2(0.9f, 0.9f);
        if (isDark)
        {
            nebulaBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(16, softColor.R, softColor.G, softColor.B)));
            nebulaBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.50f, Color.FromArgb(0, softColor.R, softColor.G, softColor.B)));
            nebulaBrush.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(14, accentColor.R, accentColor.G, accentColor.B)));
        }
        else
        {
            nebulaBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(36, accentColor.R, accentColor.G, accentColor.B)));
            nebulaBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.50f, Color.FromArgb(0, accentColor.R, accentColor.G, accentColor.B)));
            nebulaBrush.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(30, softColor.R, softColor.G, softColor.B)));
        }
        _nebulaGlow.Brush = nebulaBrush;

        if (ActualWidth > 0 && ActualHeight > 0)
        {
            _rootVisual.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
            _nebulaGlow.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
        }

        _rootVisual.Children.InsertAtBottom(_nebulaGlow);
        _rootVisual.Children.InsertAtTop(_starContainer);
        _rootVisual.Children.InsertAtTop(_meteorContainer);

        _starVisuals = new StarVisual[Stars.Length];
        for (var i = 0; i < Stars.Length; i++)
        {
            _starVisuals[i] = new StarVisual(compositor, softColor, accentColor, starCore, starCorona, isDark, Stars[i]);
            _starContainer.Children.InsertAtTop(_starVisuals[i].Layer);
            _starVisuals[i].StartTwinkle(compositor, Stars[i]);
        }

        _meteorVisuals = new MeteorVisual[MaxMeteorCount];
        for (var i = 0; i < _meteorVisuals.Length; i++)
        {
            _meteorVisuals[i] = new MeteorVisual(compositor, accentColor, softColor, isDark);
            _meteorContainer.Children.InsertAtTop(_meteorVisuals[i].Layer);
        }

        ElementCompositionPreview.SetElementChildVisual(this, _rootVisual);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        EnsureCompositionTree();
        RefreshClip();
        UpdateStarPositions();
        SyncActivity();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        StopAndClear();
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
        UpdateStarPositions();
        Visibility = Visibility.Visible;
        if (!_timer.IsEnabled)
        {
            ResetSequence();
            _clock.Restart();
            _lastClockSeconds = 0;
            _timer.Start();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RefreshClip();
        if (_rootVisual is not null)
        {
            _rootVisual.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
        }
        if (_nebulaGlow is not null)
        {
            _nebulaGlow.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
        }

        UpdateStarPositions();

        if (!_isActive || !_isLoaded || _disposed) return;

        StopAndClear();
        SyncActivity();
    }

    private void UpdateStarPositions()
    {
        if (_starVisuals is null) return;
        var width = (float)ActualWidth;
        var height = (float)ActualHeight;
        if (width <= 0 || height <= 0) return;

        for (var i = 0; i < Stars.Length; i++)
        {
            _starVisuals[i].UpdatePosition(Stars[i], width, height);
        }
    }

    private void OnTimerTick(object? sender, object e)
    {
        if (!_isActive || _disposed) return;

        var elapsedSeconds = _clock.Elapsed.TotalSeconds;
        var delta = elapsedSeconds - _lastClockSeconds;
        _lastClockSeconds = elapsedSeconds;
        if (delta <= 0) return;

        _phaseElapsed += delta;

        switch (_phase)
        {
            case Phase.Idle:
                StartBurst();
                break;
            case Phase.Burst:
                TickBurst();
                break;
            case Phase.Rest:
                if (_phaseElapsed >= _currentRestDuration)
                {
                    StartBurst();
                }
                break;
        }
    }

    private void StartBurst()
    {
        _phase = Phase.Burst;
        _phaseElapsed = 0;

        if (ActualWidth <= 0 || ActualHeight <= 0 || _meteorVisuals is null)
        {
            StartRest();
            return;
        }

        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        var spec1 = GenerateDynamicMeteorSpec();

        var visual1 = _meteorVisuals[_meteorPoolIndex % MaxMeteorCount];
        _meteorPoolIndex++;

        visual1.Launch(compositor, spec1, (float)ActualWidth, (float)ActualHeight, () =>
        {
            StartRest();
        });

        // 75% chance of second meteor
        if (Random.Shared.NextDouble() < 0.75)
        {
            var spec2 = GenerateDynamicMeteorSpec();
            var visual2 = _meteorVisuals[_meteorPoolIndex % MaxMeteorCount];
            _meteorPoolIndex++;
            visual2.Launch(compositor, spec2, (float)ActualWidth, (float)ActualHeight, null);
        }

        // 45% chance of third meteor (rich celestial meteor shower)
        if (Random.Shared.NextDouble() < 0.45)
        {
            var spec3 = GenerateDynamicMeteorSpec();
            var visual3 = _meteorVisuals[_meteorPoolIndex % MaxMeteorCount];
            _meteorPoolIndex++;
            visual3.Launch(compositor, spec3, (float)ActualWidth, (float)ActualHeight, null);
        }
    }

    private void TickBurst() { }

    private MeteorSpec GenerateDynamicMeteorSpec()
    {
        bool startFromTop = Random.Shared.NextDouble() < 0.60;
        double startX, startY;

        if (startFromTop)
        {
            startX = -0.30 + (Random.Shared.NextDouble() * 1.30);
            startY = -0.30 - (Random.Shared.NextDouble() * 0.15);
        }
        else
        {
            startX = -0.30 - (Random.Shared.NextDouble() * 0.15);
            startY = -0.10 + (Random.Shared.NextDouble() * 0.85);
        }

        double angleRad = (18.0 + (Random.Shared.NextDouble() * 24.0)) * (Math.PI / 180.0);
        double flightDist = 2.40 + (Random.Shared.NextDouble() * 0.80);

        double endX = startX + (Math.Cos(angleRad) * flightDist);
        double endY = startY + (Math.Sin(angleRad) * flightDist);

        bool isDark = ActualTheme == ElementTheme.Dark
            || (ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        double travelSeconds = 2.40 + (Random.Shared.NextDouble() * 0.60);
        float scale = 0.75f + (float)(Random.Shared.NextDouble() * 0.25);
        float opacity = isDark
            ? 0.82f + (float)(Random.Shared.NextDouble() * 0.15)
            : 0.78f + (float)(Random.Shared.NextDouble() * 0.14);
        float tailLength = 460f + (float)(Random.Shared.NextDouble() * 180f);

        return new MeteorSpec(0.00, startX, startY, endX, endY, travelSeconds, scale, opacity, tailLength);
    }

    private void StartRest()
    {
        _phase = Phase.Rest;
        _phaseElapsed = 0;
        _currentRestDuration = 0.35 + (Random.Shared.NextDouble() * 0.65);
    }

    private void ResetSequence()
    {
        _phase = Phase.Idle;
        _phaseElapsed = 0;
        _meteorPoolIndex = 0;
        _currentRestDuration = 1.5;
        if (_meteorVisuals is not null)
        {
            foreach (var visual in _meteorVisuals)
            {
                visual.Hide();
            }
        }
    }

    private void RefreshClip()
    {
        Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, ActualWidth, ActualHeight)
        };
    }

    private (Color Accent, Color Soft, Color StarCore, Color StarCorona, bool IsDark) GetThemeColors()
    {
        bool isDark = ActualTheme == ElementTheme.Dark
            || (ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        Color accent = (Application.Current.Resources["RuAccentBrush"] as SolidColorBrush)?.Color
            ?? Color.FromArgb(255, 168, 75, 56);
        Color soft = (Application.Current.Resources["RuAccentSoftBrush"] as SolidColorBrush)?.Color
            ?? Color.FromArgb(150, 168, 75, 56);

        if (isDark)
        {
            return (accent, soft, Color.FromArgb(255, 255, 255, 255), accent, true);
        }
        else
        {
            // Light mode: Vibrant radiant copper-amber crystals
            return (accent, soft, Color.FromArgb(220, accent.R, accent.G, accent.B), Color.FromArgb(140, accent.R, accent.G, accent.B), false);
        }
    }

    private void StopAndClear()
    {
        _timer.Stop();
        _clock.Stop();
        _lastClockSeconds = 0;
        ResetSequence();
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

    private enum Phase { Idle, Burst, Rest }

    private readonly record struct StarSpec(double X, double Y, float Size, double TwinkleSpeed, double TwinklePhase, float MaxOpacity, bool IsMajor = false);

    private sealed class StarVisual
    {
        public ContainerVisual Layer { get; }
        private readonly SpriteVisual _visual;
        private readonly bool _isDark;

        public StarVisual(Compositor compositor, Color softColor, Color accentColor, Color starCore, Color starCorona, bool isDark, StarSpec spec)
        {
            _isDark = isDark;
            Layer = compositor.CreateContainerVisual();
            _visual = compositor.CreateSpriteVisual();

            var radialBrush = compositor.CreateRadialGradientBrush();
            radialBrush.EllipseCenter = new Vector2(0.5f, 0.5f);
            radialBrush.EllipseRadius = new Vector2(0.5f, 0.5f);

            if (isDark)
            {
                if (spec.IsMajor)
                {
                    radialBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(255, 255, 255, 255)));
                    radialBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.25f, Color.FromArgb(200, 255, 255, 255)));
                    radialBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.55f, Color.FromArgb(120, accentColor.R, accentColor.G, accentColor.B)));
                    radialBrush.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, accentColor.R, accentColor.G, accentColor.B)));
                }
                else
                {
                    radialBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(240, 255, 255, 255)));
                    radialBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.40f, Color.FromArgb(140, softColor.R, softColor.G, softColor.B)));
                    radialBrush.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, softColor.R, softColor.G, softColor.B)));
                }
            }
            else
            {
                if (spec.IsMajor)
                {
                    // Light Major: Radiant gold-copper nucleus with amber halo
                    radialBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(240, 220, 140, 70)));
                    radialBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.30f, Color.FromArgb(190, accentColor.R, accentColor.G, accentColor.B)));
                    radialBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.65f, Color.FromArgb(90, accentColor.R, accentColor.G, accentColor.B)));
                    radialBrush.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, accentColor.R, accentColor.G, accentColor.B)));
                }
                else
                {
                    // Light Regular: Glowing warm copper crystal
                    radialBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(180, accentColor.R, accentColor.G, accentColor.B)));
                    radialBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.45f, Color.FromArgb(85, accentColor.R, accentColor.G, accentColor.B)));
                    radialBrush.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, accentColor.R, accentColor.G, accentColor.B)));
                }
            }

            _visual.Brush = radialBrush;
            Layer.Children.InsertAtTop(_visual);
            Layer.Opacity = 0;
        }

        public void StartTwinkle(Compositor compositor, StarSpec spec)
        {
            float maxOp = _isDark ? spec.MaxOpacity : spec.MaxOpacity * 0.80f;
            float minOp = maxOp * (_isDark ? (spec.IsMajor ? 0.20f : 0.08f) : (spec.IsMajor ? 0.25f : 0.15f));

            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.Duration = TimeSpan.FromSeconds(Math.Max(0.6, spec.TwinkleSpeed));
            anim.IterationBehavior = AnimationIterationBehavior.Forever;
            anim.Direction = AnimationDirection.Alternate;
            anim.InsertKeyFrame(0.0f, minOp);
            anim.InsertKeyFrame(1.0f, maxOp);
            Layer.StartAnimation("Opacity", anim);
        }

        public void UpdatePosition(StarSpec spec, float width, float height)
        {
            float posX = (float)(spec.X * width);
            float posY = (float)(spec.Y * height);
            float visualSize = _isDark
                ? (spec.IsMajor ? spec.Size * 2.8f : spec.Size * 1.8f)
                : (spec.IsMajor ? spec.Size * 2.4f : spec.Size * 1.5f);
            _visual.Size = new Vector2(visualSize, visualSize);
            _visual.Offset = new Vector3(-visualSize / 2f, -visualSize / 2f, 0);
            Layer.Offset = new Vector3(posX, posY, 0);
        }
    }

    private readonly record struct MeteorSpec(double DelaySeconds, double StartX, double StartY, double EndX, double EndY, double TravelSeconds, float Scale, float Opacity, float TailLength);

    private sealed class MeteorVisual
    {
        public ContainerVisual Layer { get; }
        private readonly SpriteVisual _outerMist;
        private readonly SpriteVisual _haloStreak;
        private readonly SpriteVisual _coreStreak;
        private readonly SpriteVisual _headAura;
        private readonly SpriteVisual _headCore;

        public MeteorVisual(Compositor compositor, Color accentColor, Color softColor, bool isDark)
        {
            Layer = compositor.CreateContainerVisual();
            Layer.IsVisible = false;

            _outerMist = compositor.CreateSpriteVisual();
            CompositionLinearGradientBrush mistBrush = compositor.CreateLinearGradientBrush();
            mistBrush.StartPoint = new Vector2(1.0f, 0.5f);
            mistBrush.EndPoint = new Vector2(0.0f, 0.5f);
            if (isDark)
            {
                mistBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(0, softColor.R, softColor.G, softColor.B)));
                mistBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.45f, Color.FromArgb(40, softColor.R, softColor.G, softColor.B)));
                mistBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.85f, Color.FromArgb(120, softColor.R, softColor.G, softColor.B)));
                mistBrush.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(180, accentColor.R, accentColor.G, accentColor.B)));
            }
            else
            {
                mistBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(0, accentColor.R, accentColor.G, accentColor.B)));
                mistBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.50f, Color.FromArgb(50, accentColor.R, accentColor.G, accentColor.B)));
                mistBrush.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(140, accentColor.R, accentColor.G, accentColor.B)));
            }
            _outerMist.Brush = mistBrush;

            _haloStreak = compositor.CreateSpriteVisual();
            CompositionLinearGradientBrush haloBrush = compositor.CreateLinearGradientBrush();
            haloBrush.StartPoint = new Vector2(1.0f, 0.5f);
            haloBrush.EndPoint = new Vector2(0.0f, 0.5f);
            if (isDark)
            {
                haloBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(0, softColor.R, softColor.G, softColor.B)));
                haloBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.35f, Color.FromArgb(90, softColor.R, softColor.G, softColor.B)));
                haloBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.80f, Color.FromArgb(220, accentColor.R, accentColor.G, accentColor.B)));
                haloBrush.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(255, accentColor.R, accentColor.G, accentColor.B)));
            }
            else
            {
                haloBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(0, accentColor.R, accentColor.G, accentColor.B)));
                haloBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.40f, Color.FromArgb(90, accentColor.R, accentColor.G, accentColor.B)));
                haloBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.80f, Color.FromArgb(200, accentColor.R, accentColor.G, accentColor.B)));
                haloBrush.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(255, 200, 100, 50)));
            }
            _haloStreak.Brush = haloBrush;

            _coreStreak = compositor.CreateSpriteVisual();
            CompositionLinearGradientBrush coreBrush = compositor.CreateLinearGradientBrush();
            coreBrush.StartPoint = new Vector2(1.0f, 0.5f);
            coreBrush.EndPoint = new Vector2(0.0f, 0.5f);
            if (isDark)
            {
                coreBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(0, accentColor.R, accentColor.G, accentColor.B)));
                coreBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.25f, Color.FromArgb(120, accentColor.R, accentColor.G, accentColor.B)));
                coreBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.70f, Color.FromArgb(255, accentColor.R, accentColor.G, accentColor.B)));
                coreBrush.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(255, 255, 255, 255)));
            }
            else
            {
                coreBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(0, accentColor.R, accentColor.G, accentColor.B)));
                coreBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.30f, Color.FromArgb(140, accentColor.R, accentColor.G, accentColor.B)));
                coreBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.75f, Color.FromArgb(240, accentColor.R, accentColor.G, accentColor.B)));
                coreBrush.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(255, 235, 160, 90)));
            }
            _coreStreak.Brush = coreBrush;

            _headAura = compositor.CreateSpriteVisual();
            _headAura.Brush = compositor.CreateColorBrush(isDark
                ? Color.FromArgb(220, accentColor.R, accentColor.G, accentColor.B)
                : Color.FromArgb(220, accentColor.R, accentColor.G, accentColor.B));

            _headCore = compositor.CreateSpriteVisual();
            _headCore.Brush = compositor.CreateColorBrush(isDark
                ? Color.FromArgb(255, 255, 255, 255)
                : Color.FromArgb(255, 245, 180, 110));

            Layer.Children.InsertAtBottom(_outerMist);
            Layer.Children.InsertAtTop(_haloStreak);
            Layer.Children.InsertAtTop(_coreStreak);
            Layer.Children.InsertAtTop(_headAura);
            Layer.Children.InsertAtTop(_headCore);
        }

        public void Hide()
        {
            Layer.IsVisible = false;
            Layer.Opacity = 0;
            Layer.StopAnimation("Offset");
            Layer.StopAnimation("Opacity");
        }

        public void Launch(Compositor compositor, MeteorSpec spec, float width, float height, Action? onCompleted)
        {
            float startX = (float)spec.StartX * width;
            float startY = (float)spec.StartY * height;
            float endX = (float)spec.EndX * width;
            float endY = (float)spec.EndY * height;

            float dx = endX - startX;
            float dy = endY - startY;
            float flightAngle = (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI);

            float tailLen = spec.TailLength * spec.Scale;
            float coreH = 0.85f * spec.Scale;
            float haloH = 2.20f * spec.Scale;
            float mistH = 4.20f * spec.Scale;
            float headAuraSize = 3.20f * spec.Scale;
            float headCoreSize = 1.20f * spec.Scale;

            // 1. Outer mist
            _outerMist.Size = new Vector2(tailLen * 0.75f, mistH);
            _outerMist.Offset = new Vector3(-tailLen * 0.75f, -mistH / 2f, 0);

            // 2. Halo streak
            _haloStreak.Size = new Vector2(tailLen * 0.92f, haloH);
            _haloStreak.Offset = new Vector3(-tailLen * 0.92f, -haloH / 2f, 0);

            // 3. Core beam
            _coreStreak.Size = new Vector2(tailLen, coreH);
            _coreStreak.Offset = new Vector3(-tailLen, -coreH / 2f, 0);

            // 4. Head aura
            _headAura.Size = new Vector2(headAuraSize, headAuraSize);
            _headAura.Offset = new Vector3(-headAuraSize / 2f, -headAuraSize / 2f, 0);

            // 5. Head core
            _headCore.Size = new Vector2(headCoreSize, headCoreSize);
            _headCore.Offset = new Vector3(-headCoreSize / 2f, -headCoreSize / 2f, 0);

            Layer.CenterPoint = new Vector3(0, 0, 0);
            Layer.RotationAngleInDegrees = flightAngle;
            Layer.Offset = new Vector3(startX, startY, 0);
            Layer.Opacity = spec.Opacity;
            Layer.IsVisible = true;

            // Hardware GPU KeyFrame Animations directly rendered on DWM VSync thread
            var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
            offsetAnim.Duration = TimeSpan.FromSeconds(spec.TravelSeconds);
            offsetAnim.InsertKeyFrame(0.0f, new Vector3(startX, startY, 0));
            offsetAnim.InsertKeyFrame(1.0f, new Vector3(endX, endY, 0));

            var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.Duration = TimeSpan.FromSeconds(spec.TravelSeconds);
            opacityAnim.InsertKeyFrame(0.0f, 0.0f);
            opacityAnim.InsertKeyFrame(0.04f, spec.Opacity);
            opacityAnim.InsertKeyFrame(0.96f, spec.Opacity);
            opacityAnim.InsertKeyFrame(1.0f, 0.0f);

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            Layer.StartAnimation("Offset", offsetAnim);
            Layer.StartAnimation("Opacity", opacityAnim);

            batch.Completed += (s, e) =>
            {
                Layer.IsVisible = false;
                onCompleted?.Invoke();
            };
            batch.End();
        }
    }
}
