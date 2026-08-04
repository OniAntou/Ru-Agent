using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Services;

namespace RuApp.Views
{
    public sealed partial class VoiceView : UserControl
    {
        private ApiService? _apiService;
        private bool _isUpdatingUi = false;

        public VoiceView()
        {
            this.InitializeComponent();
        }

        private VoicePlaybackService? _voicePlayback;

        public void Initialize(ApiService apiService, VoicePlaybackService voicePlayback)
        {
            _apiService = apiService;
            _voicePlayback = voicePlayback;
            _ = LoadVoiceAndVtsStatusAsync();
        }

        private async Task LoadVoiceAndVtsStatusAsync()
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
            }

            var vts = await _apiService.GetVtsStatusAsync();
            if (vts != null)
            {
                VtsStatusText.Text = vts.Connected
                    ? $"Đã kết nối model: {vts.ModelName}"
                    : "Trạng thái: Chưa kết nối";
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
            _ = SaveVoiceSettingsAsync();
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

        private async Task SaveVoiceSettingsAsync()
        {
            if (_apiService == null) return;
            string label = (VoiceProfileComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Ru (OmniVoice)";
            await _apiService.UpdateVoiceAsync(label, SpeedSlider.Value, AutoPaceToggle.IsOn, AutoReadToggle.IsOn);
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

        private async void OnConnectVtsClick(object sender, RoutedEventArgs e)
        {
            if (_apiService == null) return;
            ConnectVtsButton.IsEnabled = false;
            try
            {
                await _apiService.ConnectVtsAsync();
            }
            finally
            {
                ConnectVtsButton.IsEnabled = true;
            }
            _ = LoadVoiceAndVtsStatusAsync();
        }

        private async void OnVtsActionClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string action && _apiService != null)
            {
                await _apiService.TriggerVtsActionAsync(action);
            }
        }
    }
}