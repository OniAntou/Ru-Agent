using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RuApp.Controls;

public sealed class AsciiMeteorField : Canvas, IDisposable
{
    private const int MaxMeteorCount = 3;
    private const double TickSeconds = 0.06;
    private const double BaseOpacity = 0.22;

    private readonly DispatcherTimer _timer;
    private readonly List<Meteor> _meteors = new();
    private readonly Random _random = new();
    private bool _isLoaded;
    private bool _isActive;
    private bool _disposed;
    private double _spawnCountdown;

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
            _spawnCountdown = 1.0;
            _timer.Start();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
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

        _spawnCountdown -= TickSeconds;
        if (_spawnCountdown <= 0 && _meteors.Count < MaxMeteorCount)
        {
            SpawnMeteor();
            _spawnCountdown = 3.5 + _random.NextDouble() * 2.5;
        }

        for (var index = _meteors.Count - 1; index >= 0; index--)
        {
            var meteor = _meteors[index];
            meteor.Age += TickSeconds;
            meteor.X += meteor.VelocityX * TickSeconds;
            meteor.Y += meteor.VelocityY * TickSeconds;
            Canvas.SetLeft(meteor.Element, meteor.X);
            Canvas.SetTop(meteor.Element, meteor.Y);
            meteor.Element.Opacity = OpacityFor(meteor.Age / meteor.Lifetime);

            if (meteor.Age >= meteor.Lifetime ||
                meteor.X > ActualWidth + 48 ||
                meteor.Y > ActualHeight + 48)
            {
                Children.Remove(meteor.Element);
                _meteors.RemoveAt(index);
            }
        }
    }

    private void SpawnMeteor()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var element = new TextBlock
        {
            Text = _random.Next(2) == 0 ? "..-*" : "---*",
            FontFamily = Application.Current.Resources["RuMonoFontFamily"] as FontFamily
                ?? new FontFamily("Consolas"),
            Foreground = Application.Current.Resources["RuAccentBrush"] as Brush
                ?? new SolidColorBrush(Color.FromArgb(255, 168, 75, 56)),
            FontSize = 12 + _random.Next(4),
            IsHitTestVisible = false,
            Opacity = BaseOpacity
        };

        var meteor = new Meteor
        {
            Element = element,
            X = _random.NextDouble() * Math.Max(1, ActualWidth * 0.42) - 24,
            Y = _random.NextDouble() * Math.Max(1, ActualHeight * 0.28),
            VelocityX = 80 + _random.NextDouble() * 50,
            VelocityY = 80 + _random.NextDouble() * 60,
            Lifetime = 2.2 + _random.NextDouble() * 1.6
        };

        Canvas.SetLeft(element, meteor.X);
        Canvas.SetTop(element, meteor.Y);
        Children.Add(element);
        _meteors.Add(meteor);
    }

    private static double OpacityFor(double progress)
    {
        if (progress < 0.15) return BaseOpacity * progress / 0.15;
        if (progress > 0.80) return BaseOpacity * (1 - progress) / 0.20;
        return BaseOpacity;
    }

    private void StopAndClear()
    {
        _timer.Stop();
        foreach (var meteor in _meteors)
        {
            Children.Remove(meteor.Element);
        }
        _meteors.Clear();
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

    private sealed class Meteor
    {
        public required TextBlock Element { get; init; }
        public double X { get; set; }
        public double Y { get; set; }
        public double VelocityX { get; init; }
        public double VelocityY { get; init; }
        public double Lifetime { get; init; }
        public double Age { get; set; }
    }
}
