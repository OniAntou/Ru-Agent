using System;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace RuApp.Controls;

/// <summary>
/// Native WinUI 3 1:1 pixel-level reproduction of BackgroundPaths.
/// 72 mathematical Bézier curves matching the exact React component formula,
/// with dashed light streams continuously flowing from top-left towards bottom-right at 60 FPS.
/// </summary>
public sealed class BackgroundPathsControl : Grid, IDisposable
{
    public const double CanvasWidth = 696.0;
    public const double CanvasHeight = 316.0;
    public const int PathCount = 36;
    private const int TotalStreams = PathCount * 2; // 72 streams

    private readonly Canvas _pathCanvas;
    private readonly ScaleTransform _canvasScale = new();
    private readonly TranslateTransform _canvasTranslate = new();

    private readonly XamlPath[] _paths = new XamlPath[TotalStreams];
    private readonly double[] _speeds = new double[TotalStreams];
    private readonly double[] _offsets = new double[TotalStreams];
    private readonly double[] _strokeWidths = new double[TotalStreams];
    private readonly double[] _cycles = new double[TotalStreams];

    private readonly Stopwatch _stopwatch = new();
    private double _lastElapsedSeconds;
    private bool _isRendering;
    private bool _isDisposed;

    public BackgroundPathsControl()
    {
        IsHitTestVisible = false;

        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(_canvasScale);
        transformGroup.Children.Add(_canvasTranslate);

        _pathCanvas = new Canvas
        {
            Width = CanvasWidth,
            Height = CanvasHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = transformGroup,
            IsHitTestVisible = false
        };

        Children.Add(_pathCanvas);

        BuildPaths();

        SizeChanged += OnSizeChanged;
        Loaded += (_, _) =>
        {
            UpdateCanvasTransform();
            StartAnimations();
        };
        Unloaded += (_, _) => StopAnimations();
        ActualThemeChanged += (_, _) => UpdateBrushes();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCanvasTransform();
    }

    private void UpdateCanvasTransform()
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Exact SVG preserveAspectRatio="xMidYMid meet" mapping
        double scale = Math.Min(w / CanvasWidth, h / CanvasHeight);
        double ox = (w - CanvasWidth * scale) / 2.0;
        double oy = (h - CanvasHeight * scale) / 2.0;

        _canvasScale.ScaleX = scale;
        _canvasScale.ScaleY = scale;
        _canvasTranslate.X = ox;
        _canvasTranslate.Y = oy;

        // Clip to window bounds so paths cleanly enter/exit edges
        Clip = new RectangleGeometry { Rect = new Rect(0, 0, w, h) };
    }

    private void BuildPaths()
    {
        StopAnimations();

        _pathCanvas.Children.Clear();

        bool isDark = ActualTheme == ElementTheme.Dark ||
            (ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        Color baseColor = isDark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 15, 23, 42);

        int[] positions = { 1, -1 };
        int streamIndex = 0;

        foreach (int pos in positions)
        {
            for (int i = 0; i < PathCount; i++)
            {
                // Exact Bézier coordinates from React formula:
                // M-${380 - i * 5 * pos} -${189 + i * 6}
                // C-${380 - i * 5 * pos} -${189 + i * 6} -${312 - i * 5 * pos} ${216 - i * 6} ${152 - i * 5 * pos} ${343 - i * 6}
                // C${616 - i * 5 * pos} ${470 - i * 6} ${684 - i * 5 * pos} ${875 - i * 6} ${684 - i * 5 * pos} ${875 - i * 6}
                double startX = -(380.0 - i * 5.0 * pos);
                double startY = -(189.0 + i * 6.0);
                double c1x = -(380.0 - i * 5.0 * pos);
                double c1y = -(189.0 + i * 6.0);
                double c2x = -(312.0 - i * 5.0 * pos);
                double c2y = 216.0 - i * 6.0;
                double end1x = 152.0 - i * 5.0 * pos;
                double end1y = 343.0 - i * 6.0;

                double c3x = 616.0 - i * 5.0 * pos;
                double c3y = 470.0 - i * 6.0;
                double c4x = 684.0 - i * 5.0 * pos;
                double c4y = 875.0 - i * 6.0;
                double end2x = 684.0 - i * 5.0 * pos;
                double end2y = 875.0 - i * 6.0;

                var figure = new PathFigure
                {
                    StartPoint = new Point(startX, startY),
                    IsClosed = false,
                    IsFilled = false
                };

                figure.Segments.Add(new BezierSegment
                {
                    Point1 = new Point(c1x, c1y),
                    Point2 = new Point(c2x, c2y),
                    Point3 = new Point(end1x, end1y)
                });

                figure.Segments.Add(new BezierSegment
                {
                    Point1 = new Point(c3x, c3y),
                    Point2 = new Point(c4x, c4y),
                    Point3 = new Point(end2x, end2y)
                });

                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);

                // Styling: inner paths (higher i) are thicker and much brighter,
                // matching the radiant white convergence core in the preview image
                double normalizedI = (double)i / (PathCount - 1);
                double strokeWidth = 0.55 + normalizedI * 1.10; // 0.55px to 1.65px
                double targetAlpha = isDark
                    ? Math.Clamp(0.12 + normalizedI * 0.82, 0.10, 0.96)
                    : Math.Clamp(0.08 + normalizedI * 0.60, 0.06, 0.70);

                // Stream length and gap in virtual coordinates (curve length is ~1750px)
                double dashLength = 320.0 + (i % 8) * 35.0;
                double gapLength = 540.0 + (i % 6) * 55.0;
                double cycle = dashLength + gapLength;

                // In WinUI 3, StrokeDashArray and StrokeDashOffset are in units of StrokeThickness
                double dashUnits = dashLength / strokeWidth;
                double gapUnits = gapLength / strokeWidth;

                // Stagger initial offsets so streams flow naturally across the curves
                double initialOffset = -((i * 137.0 + (pos > 0 ? 0.0 : 420.0)) % cycle);

                var pathElement = new XamlPath
                {
                    Data = geometry,
                    Stroke = new SolidColorBrush(baseColor),
                    StrokeThickness = strokeWidth,
                    Opacity = targetAlpha,
                    StrokeDashArray = new DoubleCollection { dashUnits, gapUnits },
                    StrokeDashOffset = initialOffset / strokeWidth,
                    IsHitTestVisible = false
                };

                _pathCanvas.Children.Add(pathElement);
                _paths[streamIndex] = pathElement;
                _offsets[streamIndex] = initialOffset;
                _strokeWidths[streamIndex] = strokeWidth;
                _cycles[streamIndex] = cycle;

                // Speed in virtual pixels per second (streaming left to right)
                _speeds[streamIndex] = 42.0 + (i % 7) * 9.0 + (pos > 0 ? 6.0 : 0.0);

                streamIndex++;
            }
        }
    }

    private void OnRendering(object? sender, object e)
    {
        if (_isDisposed || !_isRendering) return;

        double currentSeconds = _stopwatch.Elapsed.TotalSeconds;
        double dt = currentSeconds - _lastElapsedSeconds;

        // Throttle updates to ~60 FPS so high-refresh displays (144Hz/240Hz) run silky-smooth without UI thread hitch
        if (dt < 0.015)
        {
            return;
        }

        _lastElapsedSeconds = currentSeconds;
        if (dt > 0.1)
        {
            dt = 0.016; // clamp on tab-out or frame hitch
        }

        for (int i = 0; i < TotalStreams; i++)
        {
            // Move forward along the curve (top-left towards bottom-right) by decreasing offset
            _offsets[i] -= _speeds[i] * dt;
            if (_offsets[i] < -100000.0)
            {
                _offsets[i] %= _cycles[i];
            }
            _paths[i].StrokeDashOffset = _offsets[i] / _strokeWidths[i];
        }
    }

    public void StartAnimations()
    {
        try
        {
            if (!_isRendering)
            {
                _isRendering = true;
                _lastElapsedSeconds = _stopwatch.Elapsed.TotalSeconds;
                _stopwatch.Start();
                CompositionTarget.Rendering += OnRendering;
            }
        }
        catch { }
    }

    public void StopAnimations()
    {
        try
        {
            if (_isRendering)
            {
                _isRendering = false;
                _stopwatch.Stop();
                CompositionTarget.Rendering -= OnRendering;
            }
        }
        catch { }
    }

    private void UpdateBrushes()
    {
        bool isDark = ActualTheme == ElementTheme.Dark ||
            (ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        Color baseColor = isDark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 15, 23, 42);
        for (int i = 0; i < TotalStreams; i++)
        {
            if (_paths[i] is { } path)
            {
                path.Stroke = new SolidColorBrush(baseColor);
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        StopAnimations();
        _pathCanvas.Children.Clear();
    }
}
