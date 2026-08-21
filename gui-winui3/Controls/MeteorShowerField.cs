using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace RuApp.Controls;

public sealed class MeteorShowerField : Canvas, IDisposable
{
    private const int MaxMeteorCount = 5;
    private const double LeadInSeconds = 1.0;
    private const double BurstDurationSeconds = 2.5;
    private const double RestSeconds = 10.0;
    private const double TickClampSeconds = 0.10;
    private const double TravelAngle = 18.0;

    private static readonly MeteorSpec[] Burst =
    {
        new(0.00, -0.16, 0.12, 0.42, 0.29, 1.10, 0.72, 0.14, 74),
        new(0.11, -0.12, 0.21, 0.88, 0.46, 0.86, 1.00, 0.28, 104),
        new(0.24, 0.04, 0.07, 0.94, 0.32, 0.98, 0.86, 0.22, 88),
        new(0.39, 0.22, 0.25, 1.06, 0.43, 1.18, 0.68, 0.12, 62),
        new(0.58, -0.04, 0.16, 0.98, 0.39, 0.82, 0.92, 0.26, 96)
    };

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly MeteorVisual[] _visuals;
    private bool _visualsAttached;
    private bool _isLoaded;
    private bool _isActive;
    private bool _disposed;
    private Phase _phase = Phase.Idle;
    private double _phaseElapsed;
    private double _lastClockSeconds;
    private int _nextMeteorIndex;

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
        _visuals = CreateVisuals();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        RefreshClip();
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

        Visibility = Visibility.Visible;
        if (!_timer.IsEnabled)
        {
            AttachVisuals();
            ResetSequence();
            _clock.Restart();
            _lastClockSeconds = 0;
            _timer.Start();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RefreshClip();
        if (!_isActive || !_isLoaded || _disposed) return;

        StopAndClear();
        SyncActivity();
    }

    private void OnTimerTick(object? sender, object e)
    {
        if (_disposed || !_isActive || !_isLoaded)
        {
            StopAndClear();
            return;
        }

        double delta = ConsumeDelta();
        _phaseElapsed += delta;

        switch (_phase)
        {
            case Phase.Idle:
                if (_phaseElapsed >= LeadInSeconds)
                {
                    StartBurst();
                }
                break;
            case Phase.Burst:
                AdvanceBurst();
                if (_phaseElapsed >= BurstDurationSeconds)
                {
                    StartRest();
                }
                break;
            case Phase.Rest:
                if (_phaseElapsed >= RestSeconds)
                {
                    StartBurst();
                }
                break;
        }
    }

    private double ConsumeDelta()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double delta = Math.Clamp(now - _lastClockSeconds, 0, TickClampSeconds);
        _lastClockSeconds = now;
        return delta;
    }

    private void StartBurst()
    {
        _phase = Phase.Burst;
        _phaseElapsed = 0;
        _nextMeteorIndex = 0;
        foreach (var visual in _visuals)
        {
            visual.Hide();
        }
    }

    private void AdvanceBurst()
    {
        while (_nextMeteorIndex < Burst.Length &&
               _phaseElapsed >= Burst[_nextMeteorIndex].DelaySeconds)
        {
            _visuals[_nextMeteorIndex].Show();
            _nextMeteorIndex++;
        }

        for (var index = 0; index < _nextMeteorIndex; index++)
        {
            var spec = Burst[index];
            double localTime = _phaseElapsed - spec.DelaySeconds;
            if (localTime < 0 || localTime > spec.TravelSeconds)
            {
                if (localTime > spec.TravelSeconds)
                {
                    _visuals[index].Hide();
                }

                continue;
            }

            _visuals[index].Apply(spec, localTime / spec.TravelSeconds, ActualWidth, ActualHeight);
        }
    }

    private void StartRest()
    {
        _phase = Phase.Rest;
        _phaseElapsed = 0;
        _nextMeteorIndex = 0;
        foreach (var visual in _visuals)
        {
            visual.Hide();
        }
    }

    private void ResetSequence()
    {
        _phase = Phase.Idle;
        _phaseElapsed = 0;
        _nextMeteorIndex = 0;
        foreach (var visual in _visuals)
        {
            visual.Hide();
        }
    }

    private void RefreshClip()
    {
        Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, ActualWidth, ActualHeight)
        };
    }

    private MeteorVisual[] CreateVisuals()
    {
        Brush accent = Application.Current.Resources["RuAccentBrush"] as Brush
            ?? new SolidColorBrush(Color.FromArgb(255, 168, 75, 56));
        Brush soft = Application.Current.Resources["RuAccentSoftBrush"] as Brush
            ?? new SolidColorBrush(Color.FromArgb(150, 168, 75, 56));

        var visuals = new MeteorVisual[MaxMeteorCount];
        for (var index = 0; index < visuals.Length; index++)
        {
            visuals[index] = new MeteorVisual(accent, soft);
        }

        return visuals;
    }

    private void AttachVisuals()
    {
        if (_visualsAttached) return;

        foreach (var visual in _visuals)
        {
            Children.Add(visual.Layer);
        }

        _visualsAttached = true;
    }

    private void RemoveVisuals()
    {
        if (!_visualsAttached) return;

        foreach (var visual in _visuals)
        {
            Children.Remove(visual.Layer);
        }

        _visualsAttached = false;
    }

    private void StopAndClear()
    {
        _timer.Stop();
        _clock.Stop();
        _lastClockSeconds = 0;
        ResetSequence();
        RemoveVisuals();
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
    }

    private enum Phase
    {
        Idle,
        Burst,
        Rest
    }

    private readonly record struct MeteorSpec(
        double DelaySeconds,
        double StartX,
        double StartY,
        double EndX,
        double EndY,
        double TravelSeconds,
        double Scale,
        double Opacity,
        double TailLength);

    private sealed class MeteorVisual
    {
        private const double HeadX = 116;
        private const double HeadY = 14;

        private readonly CompositeTransform _transform = new();
        private readonly Polygon _head;
        private readonly Line _mainTail;
        private readonly Line _middleTail;
        private readonly Line _farTail;

        public Canvas Layer { get; } = new()
        {
            Width = 132,
            Height = 28,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };

        public MeteorVisual(Brush accent, Brush soft)
        {
            _mainTail = new Line
            {
                Y1 = HeadY,
                Y2 = HeadY,
                Stroke = accent,
                StrokeThickness = 2.2
            };
            _middleTail = new Line
            {
                Y1 = HeadY,
                Y2 = HeadY,
                Stroke = soft,
                StrokeThickness = 1.6
            };
            _farTail = new Line
            {
                Y1 = HeadY,
                Y2 = HeadY,
                Stroke = soft,
                StrokeThickness = 1.0
            };
            _head = new Polygon
            {
                Fill = accent,
                Points = new PointCollection
                {
                    new Point(HeadX, 5),
                    new Point(HeadX + 9, HeadY),
                    new Point(HeadX, 23),
                    new Point(HeadX - 9, HeadY)
                }
            };

            Layer.RenderTransform = _transform;
            Layer.Children.Add(_farTail);
            Layer.Children.Add(_middleTail);
            Layer.Children.Add(_mainTail);
            Layer.Children.Add(_head);
        }

        public void Show()
        {
            Layer.Visibility = Visibility.Visible;
        }

        public void Hide()
        {
            Layer.Visibility = Visibility.Collapsed;
            Layer.Opacity = 0;
        }

        public void Apply(MeteorSpec spec, double progress, double width, double height)
        {
            double eased = EaseInOut(progress);
            double headX = Lerp(spec.StartX, spec.EndX, eased) * width;
            double headY = Lerp(spec.StartY, spec.EndY, eased) * height;
            double fade = progress < 0.10
                ? progress / 0.10
                : progress > 0.90
                    ? (1 - progress) / 0.10
                    : 1;

            double tailStart = HeadX - 4;
            double mainEnd = Math.Max(10, tailStart - spec.TailLength);
            double middleStart = tailStart - spec.TailLength * 0.48;
            double middleEnd = tailStart - spec.TailLength * 0.78;
            double farStart = tailStart - spec.TailLength * 0.74;
            double farEnd = Math.Max(4, tailStart - spec.TailLength);

            _mainTail.X1 = tailStart;
            _mainTail.X2 = mainEnd;
            _middleTail.X1 = middleStart;
            _middleTail.X2 = middleEnd;
            _farTail.X1 = farStart;
            _farTail.X2 = farEnd;

            _transform.CenterX = HeadX;
            _transform.CenterY = HeadY;
            _transform.Rotation = TravelAngle;
            _transform.ScaleX = spec.Scale;
            _transform.ScaleY = spec.Scale;
            Canvas.SetLeft(Layer, headX - HeadX);
            Canvas.SetTop(Layer, headY - HeadY);
            Layer.Opacity = spec.Opacity * fade;
        }

        private static double EaseInOut(double progress)
        {
            return progress < 0.5
                ? 4 * progress * progress * progress
                : 1 - Math.Pow(-2 * progress + 2, 3) / 2;
        }

        private static double Lerp(double start, double end, double progress)
        {
            return start + ((end - start) * progress);
        }
    }
}
