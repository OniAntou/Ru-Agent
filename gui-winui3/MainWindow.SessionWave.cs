using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuApp.Models;
using RuApp.Services;
using Windows.Foundation;

namespace RuApp;

public sealed partial class MainWindow
{
    private const double SessionWavePeriodSeconds = 1.8;
    private sealed record SessionWaveBinding(SessionInfo Session, Brush Normal, LinearGradientBrush Wave);
    private readonly Dictionary<TextBlock, SessionWaveBinding> _sessionTitleWaves = new();
    private readonly Stopwatch _sessionWaveClock = new();
    private DispatcherQueueTimer? _sessionWaveTimer;
    private bool _sessionWaveAnimationsEnabled = UiTiming.AnimationsEnabled;

    private void OnSessionTitleLoaded(object sender, RoutedEventArgs e) => AttachSessionTitle((TextBlock)sender);
    private void OnSessionTitleUnloaded(object sender, RoutedEventArgs e) => DetachSessionTitle((TextBlock)sender);
    private void OnSessionTitleDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs e)
    {
        if (sender is TextBlock { IsLoaded: true } title) AttachSessionTitle(title);
    }

    private void AttachSessionTitle(TextBlock title)
    {
        DetachSessionTitle(title);
        if (_closed || title.DataContext is not SessionInfo session) return;
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        for (int index = 0; index < 5; index++) gradient.GradientStops.Add(new GradientStop { Offset = index / 4.0 });
        _sessionTitleWaves[title] = new(session, title.Foreground, gradient);
        session.PropertyChanged += OnSessionWaveStateChanged;
        RefreshSessionTitleWaves();
    }

    private void DetachSessionTitle(TextBlock title)
    {
        if (!_sessionTitleWaves.Remove(title, out var binding)) return;
        binding.Session.PropertyChanged -= OnSessionWaveStateChanged;
        title.Foreground = binding.Normal;
        RefreshSessionTitleWaves();
    }

    private void OnSessionWaveStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionInfo.IsAgentRunning)) RefreshSessionTitleWaves();
    }

    private void RefreshSessionTitleWaves()
    {
        bool animate = false;
        foreach (var (title, binding) in _sessionTitleWaves)
        {
            title.Foreground = binding.Session.IsAgentRunning
                ? _sessionWaveAnimationsEnabled ? binding.Wave : (Brush)Application.Current.Resources["RuInfoBrush"]
                : binding.Normal;
            animate |= binding.Session.IsAgentRunning;
        }
        if (!animate || !_sessionWaveAnimationsEnabled || _closed)
        {
            _sessionWaveTimer?.Stop();
            _sessionWaveClock.Reset();
            return;
        }
        if (_sessionWaveTimer is null)
        {
            _sessionWaveTimer = DispatcherQueue.CreateTimer();
            _sessionWaveTimer.Interval = UiTiming.WaveFrame;
            _sessionWaveTimer.Tick += OnSessionWaveTick;
        }
        if (!_sessionWaveTimer.IsRunning)
        {
            _sessionWaveClock.Restart();
            _sessionWaveTimer.Start();
        }
        PaintSessionTitleWaves();
    }

    private void OnSessionWaveTick(DispatcherQueueTimer sender, object args) => PaintSessionTitleWaves();

    private void PaintSessionTitleWaves()
    {
        double phase = (_sessionWaveClock.Elapsed.TotalSeconds / SessionWavePeriodSeconds) % 1.0;
        double center = -0.35 + 1.70 * phase;
        var color = ((SolidColorBrush)Application.Current.Resources["RuInkBrush"]).Color;
        foreach (var (title, binding) in _sessionTitleWaves)
        {
            if (!binding.Session.IsAgentRunning || title.XamlRoot is null || title.ActualWidth <= 0) continue;
            foreach (var stop in binding.Wave.GradientStops)
                stop.Color = AgentActivityPalette.SampleSidebarAuroraColor(stop.Offset, center, color);
        }
    }

    private void StopSessionTitleWaves()
    {
        _sessionWaveTimer?.Stop();
        if (_sessionWaveTimer is not null) _sessionWaveTimer.Tick -= OnSessionWaveTick;
        _sessionWaveTimer = null;
        _sessionWaveClock.Reset();
        foreach (var (title, binding) in _sessionTitleWaves)
        {
            binding.Session.PropertyChanged -= OnSessionWaveStateChanged;
            title.Foreground = binding.Normal;
        }
        _sessionTitleWaves.Clear();
    }
}
