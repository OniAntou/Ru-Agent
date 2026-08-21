using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.Foundation;

namespace RuApp.Controls;

public sealed class AsciiMeteorField : Canvas, IDisposable
{
    private const int MaxMeteorCount = 1;
    private const double TickSeconds = 0.06;
    private const double InitialDelaySeconds = 1.6;
    private const double TravelSeconds = 1.8;
    private const double RestSeconds = 8.0;
    private const double BaseOpacity = 0.15;
    private const string MeteorMark = "..---*";

    private readonly DispatcherTimer _timer;
    private TextBlock? _meteorElement;
    private bool _isLoaded;
    private bool _isActive;
    private bool _isInFlight;
    private bool _disposed;
    private double _phaseRemaining;
    private double _progress;
    private double _startX;
    private double _startY;
    private double _endX;
    private double _endY;

    private int MeteorCount => _meteorElement is null ? 0 : 1;

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

    public AsciiMeteorField()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _timer.Tick += OnTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
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
            _isInFlight = false;
            _progress = 0;
            _phaseRemaining = InitialDelaySeconds;
            _timer.Start();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isActive || !_isLoaded || _disposed) return;

        Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, ActualWidth, ActualHeight)
        };
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

        if (!_isInFlight)
        {
            _phaseRemaining -= TickSeconds;
            if (_phaseRemaining <= 0)
            {
                BeginMeteor();
            }

            return;
        }

        _progress += TickSeconds / TravelSeconds;
        if (_progress >= 1)
        {
            StopMeteor();
            _phaseRemaining = RestSeconds;
            return;
        }

        UpdateMeteorPosition();
    }

    private void BeginMeteor()
    {
        if (MeteorCount >= MaxMeteorCount || ActualWidth <= 0 || ActualHeight <= 0)
        {
            _phaseRemaining = 1.0;
            return;
        }

        _startX = -56;
        _startY = Math.Clamp(ActualHeight * 0.12, 16, Math.Max(16, ActualHeight - 100));
        _endX = Math.Max(_startX + 140, ActualWidth * 0.82);
        _endY = Math.Min(ActualHeight - 36, _startY + Math.Max(64, ActualHeight * 0.18));
        if (_endY <= _startY)
        {
            _endY = _startY + 64;
        }

        _meteorElement = new TextBlock
        {
            Text = MeteorMark,
            FontFamily = Application.Current.Resources["RuMonoFontFamily"] as FontFamily
                ?? new FontFamily("Consolas"),
            Foreground = Application.Current.Resources["RuAccentBrush"] as Brush
                ?? new SolidColorBrush(Color.FromArgb(255, 168, 75, 56)),
            FontSize = 14,
            IsHitTestVisible = false,
            Opacity = 0
        };

        _progress = 0;
        _isInFlight = true;
        Canvas.SetLeft(_meteorElement, _startX);
        Canvas.SetTop(_meteorElement, _startY);
        Children.Add(_meteorElement);
        UpdateMeteorPosition();
    }

    private void UpdateMeteorPosition()
    {
        if (_meteorElement is null) return;

        double x = _startX + ((_endX - _startX) * _progress);
        double y = _startY + ((_endY - _startY) * _progress);
        Canvas.SetLeft(_meteorElement, x);
        Canvas.SetTop(_meteorElement, y);
        _meteorElement.Opacity = OpacityFor(_progress);
    }

    private void StopMeteor()
    {
        if (_meteorElement is not null)
        {
            Children.Remove(_meteorElement);
            _meteorElement = null;
        }

        _isInFlight = false;
        _progress = 0;
    }

    private static double OpacityFor(double progress)
    {
        if (progress < 0.12) return BaseOpacity * progress / 0.12;
        if (progress > 0.88) return BaseOpacity * (1 - progress) / 0.12;
        return BaseOpacity;
    }

    private void StopAndClear()
    {
        _timer.Stop();
        StopMeteor();
        _phaseRemaining = 0;
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
}
