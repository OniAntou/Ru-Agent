using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views
{
    public sealed partial class VoiceView : UserControl
    {
        private ApiService? _apiService;
        private bool _isUpdatingUi = false;
        private DispatcherTimer? _vtsConfigDebounce;
        private DispatcherTimer? _vtsStatusTimer;
        private bool _loadingVtsConfig;
        private DispatcherTimer? _vtsControlDebounce;
        private bool _syncingVtsControls;
        private enum PendingVtsControl { None, Overlay, MouseLook, Sensitivity }
        private PendingVtsControl _pendingVtsControl;

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

            _vtsConfigDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _vtsConfigDebounce.Tick += async (_, __) =>
            {
                _vtsConfigDebounce.Stop();
                await SaveVtsConfigAsync();
            };

            _vtsStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _vtsStatusTimer.Tick += async (_, __) => await PollVtsStatusAsync();
            _vtsStatusTimer.Start();
            _ = LoadVtsConfigAsync();

            _vtsControlDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _vtsControlDebounce.Tick += async (_, __) =>
            {
                _vtsControlDebounce.Stop();
                await FlushPendingVtsControlAsync();
            };
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

        private void OnVtsConfigSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_loadingVtsConfig) return;
            _vtsConfigDebounce?.Stop();
            _vtsConfigDebounce?.Start();
        }

        private async Task LoadVtsConfigAsync()
        {
            if (_apiService == null) return;
            _loadingVtsConfig = true;
            var config = await _apiService.GetVtsConfigAsync();
            if (config != null)
            {
                SwaySlider.Value = SwayAmplitudeToPercent(config.SwayAmplitudeIdle);
                HeadSlider.Value = config.HeadScale;
                EyeSlider.Value = config.EyeScale;
                MicroSlider.Value = MicroDelayToPercent(config.MicroActionMinDelay);
                PoseSlider.Value = PoseIntervalToPercent(config.PoseMinInterval);
            }
            _loadingVtsConfig = false;
        }

        private async Task SaveVtsConfigAsync()
        {
            if (_apiService == null) return;
            var config = new VtsConfigDto
            {
                SwayAmplitudeIdle = SwayPercentToAmplitude(SwaySlider.Value, idle: true),
                SwayAmplitudeSpeaking = SwayPercentToAmplitude(SwaySlider.Value, idle: false),
                HeadScale = HeadSlider.Value,
                EyeScale = EyeSlider.Value,
                MicroActionMinDelay = MicroPercentToDelay(MicroSlider.Value, min: true),
                MicroActionMaxDelay = MicroPercentToDelay(MicroSlider.Value, min: false),
                PoseMinInterval = PosePercentToInterval(PoseSlider.Value, min: true),
                PoseMaxInterval = PosePercentToInterval(PoseSlider.Value, min: false),
                BreathAmplitude = 1.5,
                BreathRate = 0.15,
            };
            await _apiService.SaveVtsConfigAsync(config);
        }

        private static double SwayPercentToAmplitude(double percent, bool idle)
        {
            double min = idle ? 5.0 : 5.0;
            double max = idle ? 30.0 : 40.0;
            return min + (max - min) * percent / 100.0;
        }

        private static double SwayAmplitudeToPercent(double amplitude)
        {
            if (amplitude <= 5.0) return 0.0;
            if (amplitude >= 30.0) return 100.0;
            return (amplitude - 5.0) / 25.0 * 100.0;
        }

        private static double MicroPercentToDelay(double percent, bool min)
        {
            double high = min ? 40.0 : 90.0;
            double low = min ? 8.0 : 22.0;
            return low + (high - low) * (100.0 - percent) / 100.0;
        }

        private static double MicroDelayToPercent(double delay)
        {
            if (delay <= 8.0) return 100.0;
            if (delay >= 40.0) return 0.0;
            return 100.0 - (delay - 8.0) / 32.0 * 100.0;
        }

        private static double PosePercentToInterval(double percent, bool min)
        {
            double high = min ? 300.0 : 600.0;
            double low = min ? 90.0 : 130.0;
            return low + (high - low) * (100.0 - percent) / 100.0;
        }

        private static double PoseIntervalToPercent(double interval)
        {
            if (interval <= 90.0) return 100.0;
            if (interval >= 300.0) return 0.0;
            return 100.0 - (interval - 90.0) / 210.0 * 100.0;
        }

        private async Task PollVtsStatusAsync()
        {
            if (_apiService == null) return;
            var status = await _apiService.GetVtsStatusAsync();
            if (status == null) return;
            _syncingVtsControls = true;
            try
            {
                if (status.Connected)
                {
                    VtsStatusText.Text = string.IsNullOrEmpty(status.ModelName)
                        ? "Trạng thái: Đã kết nối"
                        : $"Trạng thái: Đã kết nối · {status.ModelName}";
                    VtsStateText.Text = string.IsNullOrEmpty(status.State) ? "" : "Motion: " + status.State;
                }
                else
                {
                    VtsStatusText.Text = "Trạng thái: Chưa kết nối";
                    VtsStateText.Text = string.IsNullOrEmpty(status.ConnectError) ? "" : status.ConnectError;
                }

                OverlayToggle.IsOn = status.OverlayEnabled;
                MouseLookToggle.IsOn = status.MouseLookEnabled;
                MouseSensitivitySlider.IsEnabled = status.MouseLookEnabled;
                MouseSensitivitySlider.Value = status.MouseLookSensitivity * 100;
                MouseSensitivityText.Text = $"{status.MouseLookSensitivity * 100:0}%";

                bool overlayBroken = status.OverlayEnabled && !status.OverlayRunning;
                VtsOverlayErrorText.Text = "Overlay đang bật nhưng tiến trình không chạy (thiếu RuDesktopOverlay.exe?).";
                VtsOverlayErrorText.Visibility = overlayBroken ? Visibility.Visible : Visibility.Collapsed;
            }
            finally
            {
                _syncingVtsControls = false;
            }
        }

        private void OnOverlayToggleChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingVtsControls) return;
            _pendingVtsControl = PendingVtsControl.Overlay;
            _vtsControlDebounce?.Stop();
            _vtsControlDebounce?.Start();
        }

        private void OnMouseLookToggleChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingVtsControls) return;
            MouseSensitivitySlider.IsEnabled = MouseLookToggle.IsOn;
            _pendingVtsControl = PendingVtsControl.MouseLook;
            _vtsControlDebounce?.Stop();
            _vtsControlDebounce?.Start();
        }

        private void OnMouseSensitivityChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (MouseSensitivityText != null)
                MouseSensitivityText.Text = $"{e.NewValue:0}%";
            if (_syncingVtsControls || !MouseLookToggle.IsOn) return;
            _pendingVtsControl = PendingVtsControl.Sensitivity;
            _vtsControlDebounce?.Stop();
            _vtsControlDebounce?.Start();
        }

        private async Task FlushPendingVtsControlAsync()
        {
            if (_apiService == null || _pendingVtsControl == PendingVtsControl.None) return;
            var pending = _pendingVtsControl;
            _pendingVtsControl = PendingVtsControl.None;
            switch (pending)
            {
                case PendingVtsControl.Overlay:
                    await _apiService.SaveOverlayAsync(OverlayToggle.IsOn);
                    break;
                case PendingVtsControl.MouseLook:
                    await _apiService.SaveMouseLookAsync(MouseLookToggle.IsOn, MouseSensitivitySlider.Value / 100.0);
                    break;
                case PendingVtsControl.Sensitivity:
                    await _apiService.SaveMouseLookAsync(MouseLookToggle.IsOn, MouseSensitivitySlider.Value / 100.0);
                    break;
            }
        }
    }
}