using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
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
        private bool _isDraggingMode = false;
        private string _subtitlePosMode = "auto";
        private int _subtitlePosX = -1;
        private int _subtitlePosY = -1;

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
            _apiService = null;
            _voicePlayback = null;
            _overlayService = null;
        }

        private async Task LoadVoiceSettingsAsync()
        {
            if (_apiService == null) return;
            var settings = await _apiService.GetSettingsAsync();
            PopulateUi(settings);
        }

        private void PopulateUi(AppSettingsDto? settings)
        {
            _isUpdatingUi = true;

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
                SubtitleCustomizationPanel.Visibility = settings.ShowSubtitles ? Visibility.Visible : Visibility.Collapsed;

                foreach (ComboBoxItem item in SubtitleSizeCombo.Items)
                {
                    if ((item.Tag as string) == settings.SubtitleSize)
                    {
                        SubtitleSizeCombo.SelectedItem = item;
                        break;
                    }
                }

                foreach (ComboBoxItem item in SubtitleColorCombo.Items)
                {
                    if (string.Equals(item.Tag as string, settings.SubtitleColor, StringComparison.OrdinalIgnoreCase))
                    {
                        SubtitleColorCombo.SelectedItem = item;
                        break;
                    }
                }

                SubtitleDurationSlider.Value = settings.SubtitleDuration;
                SubtitleDurationLabel.Text = $"Display duration after speech: {settings.SubtitleDuration:F1}s";

                _subtitlePosMode = settings.SubtitlePosMode ?? "auto";
                _subtitlePosX = settings.SubtitlePosX;
                _subtitlePosY = settings.SubtitlePosY;
                UpdatePositionStatusUi();

                if (_overlayService != null)
                {
                    _overlayService.PositionChanged -= OnOverlayPositionChanged;
                    _overlayService.PositionChanged += OnOverlayPositionChanged;
                }

                ApplySubtitleToggle(settings.ShowSubtitles);
                _overlayService?.ApplyCustomization(
                    settings.SubtitleColor,
                    settings.SubtitleSize,
                    settings.SubtitleDuration,
                    _subtitlePosMode,
                    _subtitlePosX,
                    _subtitlePosY);
            }

            _isUpdatingUi = false;
        }

        private void UpdatePositionStatusUi()
        {
            if (SubtitlePositionStatusLabel == null) return;
            if (_subtitlePosMode == "custom" && _subtitlePosX >= 0 && _subtitlePosY >= 0)
            {
                SubtitlePositionStatusLabel.Text = $"Status: Custom position (X: {_subtitlePosX}, Y: {_subtitlePosY})";
            }
            else
            {
                SubtitlePositionStatusLabel.Text = "Status: Automatically following Live2D Avatar";
            }
        }

        private void OnOverlayPositionChanged(int x, int y)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _subtitlePosX = x;
                _subtitlePosY = y;
                _subtitlePosMode = "custom";
                UpdatePositionStatusUi();
                _ = SaveVoiceSettingsAsync();
            });
        }

        private async void OnSubtitleDragButtonClicked(object sender, RoutedEventArgs e)
        {
            _isDraggingMode = !_isDraggingMode;
            if (_isDraggingMode)
            {
                SubtitleDragButtonText.Text = "Save & Lock Position";
                SubtitleDragIcon.Glyph = "\uE73E";
                if (!SubtitleToggle.IsOn)
                {
                    SubtitleToggle.IsOn = true;
                    SubtitleCustomizationPanel.Visibility = Visibility.Visible;
                    ApplySubtitleToggle(true);
                }
                _overlayService?.SetDragMode(true);
                SubtitlePositionStatusLabel.Text = "Adjusting position: Hold left mouse button on the screen subtitle to drag.";
            }
            else
            {
                SubtitleDragButtonText.Text = "Drag to position";
                SubtitleDragIcon.Glyph = "\uE7C2";
                _overlayService?.SetDragMode(false);
                UpdatePositionStatusUi();
                await SaveVoiceSettingsAsync();
            }
        }

        private async void OnSubtitleResetPosClicked(object sender, RoutedEventArgs e)
        {
            _isDraggingMode = false;
            SubtitleDragButtonText.Text = "Drag to position";
            SubtitleDragIcon.Glyph = "\uE7C2";
            _subtitlePosMode = "auto";
            _subtitlePosX = -1;
            _subtitlePosY = -1;
            _overlayService?.SetDragMode(false);
            _overlayService?.ResetPosition();
            UpdatePositionStatusUi();
            await SaveVoiceSettingsAsync();
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
            SubtitleCustomizationPanel.Visibility = isOn ? Visibility.Visible : Visibility.Collapsed;
            if (!isOn && _isDraggingMode)
            {
                _isDraggingMode = false;
                SubtitleDragButtonText.Text = "Drag to position";
                SubtitleDragIcon.Glyph = "\uE7C2";
                _overlayService?.SetDragMode(false);
            }
            ApplySubtitleToggle(isOn);
            await SaveVoiceSettingsAsync();
        }

        private async void OnSubtitleSizeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi || _apiService == null) return;
            await SaveVoiceSettingsAsync();
        }

        private async void OnSubtitleColorChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi || _apiService == null) return;
            await SaveVoiceSettingsAsync();
        }

        private void OnSubtitleDurationChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (SubtitleDurationLabel != null)
            {
                SubtitleDurationLabel.Text = $"Display duration after speech: {e.NewValue:F1}s";
            }
            if (_isUpdatingUi || _apiService == null) return;
            _speedDebounceTimer?.Stop();
            _speedDebounceTimer?.Start();
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
            string subtitleSize = (SubtitleSizeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "medium";
            string subtitleColor = (SubtitleColorCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "#FFFFFF";
            double subtitleDuration = SubtitleDurationSlider.Value;

            await _apiService.UpdateVoiceAsync(
                label,
                SpeedSlider.Value,
                AutoPaceToggle.IsOn,
                AutoReadToggle.IsOn,
                SubtitleToggle.IsOn,
                "original",
                subtitleColor,
                subtitleSize,
                subtitleDuration,
                _subtitlePosMode,
                _subtitlePosX,
                _subtitlePosY);

            _overlayService?.ApplyCustomization(
                subtitleColor,
                subtitleSize,
                subtitleDuration,
                _subtitlePosMode,
                _subtitlePosX,
                _subtitlePosY);

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
