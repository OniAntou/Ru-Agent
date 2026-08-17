using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Services;

namespace RuApp.Views
{
    public sealed partial class VoiceView : UserControl, IDisposable
    {
        private ApiService? _apiService;
        private bool _isUpdatingUi = false;
        private VoicePlaybackService? _voicePlayback;
        private SubtitleOverlayService? _overlayService;
        private DispatcherTimer? _speedDebounceTimer;

        public VoiceView()
        {
            this.InitializeComponent();
        }

        public void Initialize(ApiService apiService, VoicePlaybackService voicePlayback, SubtitleOverlayService? overlayService = null)
        {
            _apiService = apiService;
            _voicePlayback = voicePlayback;
            _overlayService = overlayService;

            _speedDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _speedDebounceTimer.Tick += async (_, __) =>
            {
                _speedDebounceTimer.Stop();
                await SaveVoiceSettingsAsync();
            };

            _ = LoadVoiceSettingsAsync();
        }

        public void Dispose()
        {
            _speedDebounceTimer?.Stop();
            _speedDebounceTimer = null;
        }

        private async Task LoadVoiceSettingsAsync()
        {
            if (_apiService == null) return;
            _isUpdatingUi = true;

            var settings = await _apiService.GetSettingsAsync();
            if (settings != null)
            {
                foreach (ComboBoxItem item in VoiceProfileComboBox.Items)
                {
                    if ((item.Tag as string) == settings.VoiceLabel)
                    {
                        VoiceProfileComboBox.SelectedItem = item;
                        break;
                    }
                }

                SpeedSlider.Value = settings.VoiceSpeed;
                SpeedText.Text = $"{settings.VoiceSpeed:F2}x";
                AutoPaceToggle.IsOn = settings.AutoPaceEnabled;
                AutoReadToggle.IsOn = settings.AutoReadReplies;
                SubtitleToggle.IsOn = settings.ShowSubtitles;
                ApplySubtitleToggle(settings.ShowSubtitles);
            }

            _isUpdatingUi = false;
        }

        private async void OnVoiceProfileChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi || _apiService == null) return;
            await SaveVoiceSettingsAsync();
        }

        private void OnSpeedSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (SpeedText != null)
            {
                SpeedText.Text = $"{e.NewValue:F2}x";
            }
            if (_isUpdatingUi || _apiService == null) return;
            _speedDebounceTimer?.Stop();
            _speedDebounceTimer?.Start();
        }

        private async void OnAutoPaceToggled(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi || _apiService == null) return;
            await SaveVoiceSettingsAsync();
        }

        private async void OnAutoReadToggled(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi || _apiService == null) return;
            await SaveVoiceSettingsAsync();
        }

        private async void OnSubtitleToggled(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi || _apiService == null) return;
            bool isOn = SubtitleToggle.IsOn;
            ApplySubtitleToggle(isOn);
            await SaveVoiceSettingsAsync();
        }

        private void ApplySubtitleToggle(bool isOn)
        {
            if (_voicePlayback is not null)
            {
                _voicePlayback.SubtitlesEnabled = isOn;
            }
            if (_overlayService == null) return;
            if (isOn)
            {
                _overlayService.Start();
            }
            else
            {
                _overlayService.Pause();
            }
        }

        private async Task SaveVoiceSettingsAsync()
        {
            if (_apiService == null) return;
            string label = (VoiceProfileComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Ru (OmniVoice)";
            await _apiService.UpdateVoiceAsync(
                label,
                SpeedSlider.Value,
                AutoPaceToggle.IsOn,
                AutoReadToggle.IsOn,
                SubtitleToggle.IsOn,
                "original");
            if (_voicePlayback != null)
            {
                await _voicePlayback.RefreshSettingsAsync();
            }
        }

        private async void OnWarmupClick(object sender, RoutedEventArgs e)
        {
            if (_apiService == null) return;
            WarmupButton.IsEnabled = false;
            try
            {
                await _apiService.WarmupVoiceAsync();
            }
            finally
            {
                WarmupButton.IsEnabled = true;
            }
        }

        private async void OnReleaseClick(object sender, RoutedEventArgs e)
        {
            if (_apiService == null) return;
            ReleaseButton.IsEnabled = false;
            try
            {
                await _apiService.ReleaseVoiceAsync();
            }
            finally
            {
                ReleaseButton.IsEnabled = true;
            }
        }

        private async void OnTestVoiceClick(object sender, RoutedEventArgs e)
        {
            if (_voicePlayback == null) return;
            string text = TestTextBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;
            TestVoiceButton.IsEnabled = false;
            try
            {
                await _voicePlayback.SpeakTextAsync(text);
            }
            finally
            {
                TestVoiceButton.IsEnabled = true;
            }
        }
    }
}
