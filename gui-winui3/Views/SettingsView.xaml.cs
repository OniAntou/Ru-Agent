using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views
{
    public sealed partial class SettingsView : UserControl, IDisposable
    {
        private ApiService? _apiService;
        private bool _loading = false;
        private List<string> _allModels = new();
        private DispatcherTimer? _autoSaveTimer;
        private bool _saveInProgress;
        private bool _saveRequestedDuringSave;
        private bool _filteringModels;
        private DispatcherTimer? _vtsConfigDebounce;
        private bool _loadingVtsConfig;
        private DispatcherTimer? _vtsControlDebounce;
        private bool _syncingVtsControls;
        private enum PendingVtsControl { None, Overlay, MouseLook, Sensitivity }
        private PendingVtsControl _pendingVtsControl;

        private static readonly Dictionary<string, string[]> FallbackModels = new()
        {
            ["opencode"] = Array.Empty<string>(),
            ["kilo"] = Array.Empty<string>(),
            ["gguf"] = new[] { "local-model-gguf" }
        };

        public SettingsView()
        {
            this.InitializeComponent();
        }

        public void Initialize(ApiService apiService)
        {
            _apiService = apiService;
            MemoryControl.Initialize(apiService);
            _ = LoadSettingsAsync();
            SetupAutoSave();
            SetupVts();
        }

        private void SetupVts()
        {
            _vtsConfigDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _vtsConfigDebounce.Tick += async (_, __) =>
            {
                _vtsConfigDebounce.Stop();
                await SaveVtsConfigAsync();
                await LoadVtsConfigAsync();
            };

            _vtsControlDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _vtsControlDebounce.Tick += async (_, __) =>
            {
                _vtsControlDebounce.Stop();
                await FlushPendingVtsControlAsync();
            };

            _ = LoadVtsConfigAsync();
            _ = PollVtsStatusAsync();
        }

        private void SetupAutoSave()
        {
            _autoSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            _autoSaveTimer.Tick += async (_, __) =>
            {
                _autoSaveTimer.Stop();
                await SaveSettingsAsync();
            };

            BackendComboBox.SelectionChanged += (_, __) => ScheduleAutoSave();
            ModelComboBox.SelectionChanged += (_, __) =>
            {
                if (!_filteringModels) ScheduleAutoSave();
            };
            OpenCodeApiKeyBox.PasswordChanged += (_, __) => ScheduleAutoSave();
            KiloApiKeyBox.PasswordChanged += (_, __) => ScheduleAutoSave();
            GeminiVisionKeyBox.PasswordChanged += (_, __) => ScheduleAutoSave();
            CheckpointPathBox.TextChanged += (_, __) => ScheduleAutoSave();
            ApiVariantComboBox.SelectionChanged += (_, __) => ScheduleAutoSave();
            TemperatureNumberBox.TextChanged += (_, __) => ScheduleAutoSave();
            TopKNumberBox.TextChanged += (_, __) => ScheduleAutoSave();
            MaxTokensNumberBox.TextChanged += (_, __) => ScheduleAutoSave();
            AllowOnlineSearchToggle.Toggled += (_, __) => ScheduleAutoSave();
            SelfLearningToggle.Toggled += (_, __) => ScheduleAutoSave();
            CloudMemoryConsentToggle.Toggled += (_, __) => ScheduleAutoSave();
            KiloReasoningToggle.Toggled += (_, __) => ScheduleAutoSave();
        }

        private void ScheduleAutoSave()
        {
            if (_apiService == null || _loading) return;
            _autoSaveTimer?.Stop();
            _autoSaveTimer?.Start();
            AutoSaveStatusText.Text = "Đang lưu...";
        }

        private async Task LoadSettingsAsync()
        {
            if (_apiService == null) return;
            _loading = true;
            var settings = await _apiService.GetSettingsAsync();
            if (settings != null)
            {
                string backendChoice = settings.BackendChoice == "transformers" ? "gguf" : settings.BackendChoice;
                foreach (ComboBoxItem item in BackendComboBox.Items)
                {
                    if ((item.Tag as string) == backendChoice)
                    {
                        BackendComboBox.SelectedItem = item;
                        break;
                    }
                }

                OpenCodeApiKeyBox.Password = string.Empty;
                OpenCodeApiKeyBox.PlaceholderText = settings.HasOpenCodeKey
                    ? "Đã lưu (để trống để giữ nguyên)"
                    : "sk-opencode-...";
                KiloApiKeyBox.Password = string.Empty;
                KiloApiKeyBox.PlaceholderText = settings.HasKiloKey
                    ? "Đã lưu (để trống để giữ nguyên)"
                    : "sk-kilo-...";
                GeminiVisionKeyBox.Password = string.Empty;
                GeminiVisionKeyBox.PlaceholderText = settings.HasGeminiVisionKey
                    ? "Đã lưu (để trống để giữ nguyên)"
                    : "AIza...";
                CheckpointPathBox.Text = settings.CheckpointPath;

                foreach (ComboBoxItem item in ApiVariantComboBox.Items)
                {
                    if ((item.Tag as string) == settings.ApiVariant)
                    {
                        ApiVariantComboBox.SelectedItem = item;
                        break;
                    }
                }

                TemperatureNumberBox.Text = settings.Temperature.ToString("F2");
                TopKNumberBox.Text = settings.TopK.ToString();
                MaxTokensNumberBox.Text = settings.MaxNewTokens.ToString();

                AllowOnlineSearchToggle.IsOn = settings.AllowOnlineSearch;
                SelfLearningToggle.IsOn = settings.SelfLearningEnabled;
                CloudMemoryConsentToggle.IsOn = settings.CloudMemoryConsent;
                KiloReasoningToggle.IsOn = settings.KiloReasoningEnabled;

                await LoadModelsForBackendAsync(settings.BackendChoice, settings, settings.OpenCodeModel, settings.KiloModel);
                UpdateVisibilityForBackend(settings.BackendChoice);
            }
            _loading = false;
        }

        private async Task LoadModelsForBackendAsync(string backend, AppSettingsDto? settings, string fallbackOpenCode, string fallbackKilo)
        {
            if (_apiService == null || !(backend == "opencode" || backend == "kilo")) return;

            ModelLoadingText.Visibility = Visibility.Visible;

            string chosen = backend == "opencode" ? (settings?.OpenCodeModel ?? fallbackOpenCode) : (settings?.KiloModel ?? fallbackKilo);

            var models = await _apiService.GetModelsAsync(backend);
            ModelLoadingText.Visibility = Visibility.Collapsed;

            if (models.Count == 0)
            {
                models = new List<string>(FallbackModels[backend]);
            }

            _allModels = models;
            FilterModels(chosen);
        }

        private void FilterModels(string? selectedModel = null)
        {
            _filteringModels = true;
            try
            {
                string searchText = ModelSearchBox.Text?.Trim().ToLower() ?? "";
                ModelComboBox.Items.Clear();

                var filteredModels = string.IsNullOrEmpty(searchText)
                    ? _allModels
                    : _allModels.Where(m => MatchesSearch(m.ToLower(), searchText)).ToList();

                foreach (string model in filteredModels)
                {
                    ModelComboBox.Items.Add(model);
                }

                if (selectedModel != null && filteredModels.Contains(selectedModel))
                {
                    ModelComboBox.SelectedItem = selectedModel;
                }
                else if (ModelComboBox.Items.Count > 0)
                {
                    ModelComboBox.SelectedIndex = 0;
                }
            }
            finally
            {
                _filteringModels = false;
            }
        }

        private bool MatchesSearch(string modelName, string searchText)
        {
            var keywords = searchText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (keywords.Length == 0) return true;
            return keywords.All(keyword => modelName.Contains(keyword));
        }

        private void OnModelSearchBoxTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            string? currentSelection = ModelComboBox.SelectedItem as string;
            FilterModels(currentSelection);
        }

        private void UpdateVisibilityForBackend(string choice)
        {
            bool cloud = choice == "opencode" || choice == "kilo";

            OpenCodeApiRow.Visibility = (choice == "opencode") ? Visibility.Visible : Visibility.Collapsed;
            ApiVariantRow.Visibility = (choice == "opencode") ? Visibility.Visible : Visibility.Collapsed;
            KiloApiRow.Visibility = (choice == "kilo") ? Visibility.Visible : Visibility.Collapsed;
            KiloReasoningRow.Visibility = (choice == "kilo") ? Visibility.Visible : Visibility.Collapsed;

            CloudMemoryRow.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
            ModelRow.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
            OnlineSearchRow.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
            SelfLearningRow.Visibility = (choice == "kilo") ? Visibility.Visible : Visibility.Collapsed;
            ExtendedFeaturesCard.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void OnBackendSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || BackendComboBox.SelectedItem == null) return;
            string choice = (BackendComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "gguf";

            UpdateVisibilityForBackend(choice);

            bool cloud = choice == "opencode" || choice == "kilo";
            if (cloud)
            {
                ModelLoadingText.Visibility = Visibility.Visible;
                ModelComboBox.Items.Clear();
                ModelComboBox.SelectedItem = null;
                ModelSearchBox.Text = string.Empty;

                if (_apiService != null)
                {
                    var models = await _apiService.GetModelsAsync(choice);
                    if (models.Count == 0) models = new List<string>(FallbackModels[choice]);
                    _allModels = models;
                    FilterModels();
                }
                ModelLoadingText.Visibility = Visibility.Collapsed;
            }
        }

        private string? SelectedModelText()
        {
            if (ModelComboBox.SelectedItem is string selected)
            {
                return selected;
            }
            return null;
        }

        private async Task SaveSettingsAsync()
        {
            if (_apiService == null) return;
            if (_saveInProgress)
            {
                _saveRequestedDuringSave = true;
                return;
            }
            _saveInProgress = true;

            try
            {
                string backend = (BackendComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "gguf";
                string modelChoice = SelectedModelText() ?? "";

                var dto = new AppSettingsDto
                {
                    BackendChoice = backend,
                    OpenCodeApiKey = OpenCodeApiKeyBox.Password,
                    KiloApiKey = KiloApiKeyBox.Password,
                    GeminiVisionApiKey = GeminiVisionKeyBox.Password,
                    ApiVariant = (ApiVariantComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Default",
                    OpenCodeModel = backend == "opencode" ? (modelChoice ?? "") : "",
                    KiloModel = backend == "kilo" ? (modelChoice ?? "") : "",
                    CheckpointPath = CheckpointPathBox.Text,
                    Temperature = double.TryParse(TemperatureNumberBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double temp) ? temp : 0.7,
                    TopK = int.TryParse(TopKNumberBox.Text, out int topK) ? topK : 40,
                    MaxNewTokens = int.TryParse(MaxTokensNumberBox.Text, out int maxTokens) ? maxTokens : 512,
                    AllowOnlineSearch = AllowOnlineSearchToggle.IsOn,
                    SelfLearningEnabled = SelfLearningToggle.IsOn,
                    CloudMemoryConsent = CloudMemoryConsentToggle.IsOn,
                    KiloReasoningEnabled = KiloReasoningToggle.IsOn
                };

                bool ok = await _apiService.SaveSettingsAsync(dto);
                AutoSaveStatusText.Text = ok ? "Đã lưu" : "Lưu thất bại";
                if (!ok)
                {
                    System.Diagnostics.Debug.WriteLine("[Settings] Auto-save failed");
                }
            }
            catch (Exception ex)
            {
                AutoSaveStatusText.Text = "Lỗi lưu";
                System.Diagnostics.Debug.WriteLine($"[Settings] Auto-save error: {ex.Message}");
            }
            finally
            {
                _saveInProgress = false;
                if (_saveRequestedDuringSave)
                {
                    _saveRequestedDuringSave = false;
                    _autoSaveTimer?.Stop();
                    _autoSaveTimer?.Start();
                }
            }
        }

        public void Dispose()
        {
            _autoSaveTimer?.Stop();
            _vtsConfigDebounce?.Stop();
            _vtsControlDebounce?.Stop();
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
            _ = PollVtsStatusAsync();
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
            System.Diagnostics.Debug.WriteLine($"[SettingsView] Slider changed: {sender?.GetType().Name} new={e.NewValue:0}");
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
            System.Diagnostics.Debug.WriteLine($"[SettingsView] SaveVtsConfigAsync: sway={config.SwayAmplitudeIdle:0.0}/{config.SwayAmplitudeSpeaking:0.0} head={config.HeadScale:0.0} eye={config.EyeScale:0.0}");
            bool ok = await _apiService.SaveVtsConfigAsync(config);
            System.Diagnostics.Debug.WriteLine($"[SettingsView] SaveVtsConfigAsync result={ok}");
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
                MouseLookToggle.IsEnabled = status.Connected;
                MouseLookToggle.IsOn = status.MouseLookEnabled;
                MouseSensitivitySlider.IsEnabled = status.MouseLookEnabled && status.Connected;
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
