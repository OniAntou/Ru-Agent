using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views
{
    public sealed partial class SettingsView : UserControl
    {
        private ApiService? _apiService;
        private ScreenObserverService? _observerService;
        private bool _loading = false;
        private List<string> _allModels = new();
        private DispatcherTimer? _autoSaveTimer;
        private bool _saveInProgress;

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

        public void Initialize(ApiService apiService, ScreenObserverService observerService)
        {
            _apiService = apiService;
            _observerService = observerService;
            MemoryControl.Initialize(apiService);
            _ = LoadSettingsAsync();
            SetupAutoSave();
            SetupObservation();
        }

        private void SetupObservation()
        {
            if (_observerService == null) return;
            _observerService.OnMetricsUpdated += OnObservationMetricsUpdated;
            UpdateObservationUiState();
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
            ModelComboBox.SelectionChanged += (_, __) => ScheduleAutoSave();
            ModelSearchBox.TextChanged += (_, __) => ScheduleAutoSave();
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
        }

        private void ScheduleAutoSave()
        {
            if (_apiService == null || _loading) return;
            _autoSaveTimer?.Stop();
            _autoSaveTimer?.Start();
            AutoSaveStatusText.Text = "Đang lưu...";
        }

        private void OnObservationMetricsUpdated(ObservationMetricsDto metrics)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                FrameCountText.Text = metrics.FrameCount.ToString();
                VisionCountText.Text = metrics.VisionCount.ToString();
                EmittedCountText.Text = metrics.EmittedCount.ToString();
                StaleDropsText.Text = metrics.StaleDrops.ToString();
                LatencyText.Text = $"{metrics.LastMotionDelta:F2}% | {metrics.Engine} ({metrics.MsBefore} ms)";
                UpdateObservationUiState();
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

            UpdateObservationUiState();
        }

        private void UpdateObservationUiState()
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

        private async Task LoadSettingsAsync()
        {
            if (_apiService == null) return;
            _loading = true;
            var settings = await _apiService.GetSettingsAsync();
            if (settings != null)
            {
                // The HF Transformers backend is retired (training only); a stale
                // saved "transformers" choice falls back to the local GGUF backend.
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

        private bool MatchesSearch(string modelName, string searchText)
        {
            // Split search text into keywords
            var keywords = searchText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (keywords.Length == 0)
                return true;

            // Model must contain ALL keywords (in any order)
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

            // These are only for cloud
            CloudMemoryRow.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
            ModelRow.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
            OnlineSearchRow.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
            SelfLearningRow.Visibility = (choice == "kilo") ? Visibility.Visible : Visibility.Collapsed;
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
            if (_apiService == null || _saveInProgress) return;
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
                    CloudMemoryConsent = CloudMemoryConsentToggle.IsOn
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
            }
        }
    }
}