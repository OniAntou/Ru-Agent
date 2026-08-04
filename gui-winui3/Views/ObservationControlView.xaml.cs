using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views
{
    public sealed partial class ObservationControlView : UserControl
    {
        private ScreenObserverService? _observerService;

        public ObservationControlView()
        {
            this.InitializeComponent();
        }

        public void Initialize(ScreenObserverService observerService)
        {
            _observerService = observerService;
            _observerService.OnMetricsUpdated += OnMetricsUpdated;
            UpdateUiState();
        }

        private void OnMetricsUpdated(ObservationMetricsDto metrics)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                FrameCountText.Text = metrics.FrameCount.ToString();
                VisionCountText.Text = metrics.VisionCount.ToString();
                EmittedCountText.Text = metrics.EmittedCount.ToString();
                StaleDropsText.Text = metrics.StaleDrops.ToString();
                LatencyText.Text = $"{metrics.LastMotionDelta:F2}% | {metrics.Engine} ({metrics.MsBefore} ms)";
                UpdateUiState();
            });
        }

        private async void OnToggleObservationClick(object sender, RoutedEventArgs e)
        {
            if (_observerService == null) return;
            ToggleObservationButton.IsEnabled = false;

            try
            {
                if (_observerService.IsActive)
                {
                    await _observerService.StopAsync();
                }
                else
                {
                    await _observerService.StartAsync();
                }
            }
            finally
            {
                ToggleObservationButton.IsEnabled = true;
            }

            UpdateUiState();
        }

        private void UpdateUiState()
        {
            bool active = _observerService?.IsActive ?? false;
            if (active)
            {
                ToggleText.Text = "Dừng quan sát";
                ToggleIcon.Glyph = "\uE71A";
            }
            else
            {
                ToggleText.Text = "Bắt đầu quan sát";
                ToggleIcon.Glyph = "\uE714";
            }
        }
    }
}