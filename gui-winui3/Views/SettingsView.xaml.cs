using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuAgent.Harness.Core;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views
{
    public sealed partial class SettingsView : UserControl, IDisposable
    {
        private ApiService? _apiService;
        private ProviderCatalogController? _providerCatalogController;
        private bool _ownsProviderCatalogController;
        private bool _loading = false;
        private DispatcherTimer? _autoSaveTimer;
        private bool _saveInProgress;
        private bool _saveRequestedDuringSave;

        public event Action<UiTheme>? ThemeChanged;
        public event Action<AgentPermissionPreset>? PresetChanged;

        private string _themeChoice = "dark";
        private string _presetChoice = "standard";
        private string _providerChoice = "opencode";

        private sealed record DropdownOption(Button Button, TextBlock Label, FontIcon Check, string Value);
        private IReadOnlyList<DropdownOption>? _themeOptions;
        private IReadOnlyList<DropdownOption>? _presetOptions;
        private IReadOnlyList<DropdownOption>? _providerOptions;

        private static readonly HashSet<string> KnownProviders = new(StringComparer.OrdinalIgnoreCase)
        {
            "opencode", "kilo", "openai", "anthropic", "custom",
        };

        public void SetCurrentTheme(UiTheme theme)
        {
            _themeChoice = theme == UiTheme.Light ? "light" : "dark";
            RefreshThemeUI();
        }

        public void SetCurrentPreset(AgentPermissionPreset preset)
        {
            _presetChoice = preset == AgentPermissionPreset.Trusted ? "trusted" : "standard";
            RefreshPresetUI();
        }

        public void SetCurrentProvider(string backendChoice)
        {
            _providerChoice = KnownProviders.Contains(backendChoice ?? string.Empty)
                ? backendChoice!
                : "opencode";
            RefreshProviderUI();
        }

        private void RefreshThemeUI()
        {
            if (_themeOptions == null || ThemeButtonLabel == null) return;
            RefreshDropdownUI(_themeOptions, _themeChoice, ThemeButtonLabel);
        }

        private void RefreshPresetUI()
        {
            if (_presetOptions == null || PresetButtonLabel == null) return;
            RefreshDropdownUI(_presetOptions, _presetChoice, PresetButtonLabel);
        }

        private void RefreshProviderUI()
        {
            if (_providerOptions == null || ProviderButtonLabel == null) return;
            RefreshDropdownUI(_providerOptions, _providerChoice, ProviderButtonLabel);
        }

        private static void RefreshDropdownUI(
            IReadOnlyList<DropdownOption> options,
            string current,
            TextBlock triggerLabel)
        {
            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var selectedBrush = (Brush)Application.Current.Resources["RuAccentSoftBrush"];
            foreach (var option in options)
            {
                bool isSelected = string.Equals(option.Value, current, StringComparison.OrdinalIgnoreCase);
                option.Button.Background = isSelected ? selectedBrush : transparent;
                option.Check.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
                option.Label.FontWeight = isSelected
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal;
                option.Label.Opacity = isSelected ? 1.0 : 0.88;
                if (isSelected)
                {
                    triggerLabel.Text = option.Label.Text;
                }
            }
        }

        private void InitDropdownOptions()
        {
            _themeOptions = new[]
            {
                new DropdownOption(ThemeDarkOption, ThemeDarkText, ThemeDarkCheck, "dark"),
                new DropdownOption(ThemeLightOption, ThemeLightText, ThemeLightCheck, "light"),
            };
            _presetOptions = new[]
            {
                new DropdownOption(PresetStandardOption, PresetStandardText, PresetStandardCheck, "standard"),
                new DropdownOption(PresetTrustedOption, PresetTrustedText, PresetTrustedCheck, "trusted"),
            };
            _providerOptions = new[]
            {
                new DropdownOption(ProviderOpencodeOption, ProviderOpencodeText, ProviderOpencodeCheck, "opencode"),
                new DropdownOption(ProviderKiloOption, ProviderKiloText, ProviderKiloCheck, "kilo"),
                new DropdownOption(ProviderOpenaiOption, ProviderOpenaiText, ProviderOpenaiCheck, "openai"),
                new DropdownOption(ProviderAnthropicOption, ProviderAnthropicText, ProviderAnthropicCheck, "anthropic"),
                new DropdownOption(ProviderCustomOption, ProviderCustomText, ProviderCustomCheck, "custom"),
            };
            foreach (var options in new[] { _themeOptions, _presetOptions, _providerOptions })
            {
                AttachDropdownHover(options);
            }
            RefreshThemeUI();
            RefreshPresetUI();
            RefreshProviderUI();
        }

        private static void AttachDropdownHover(IReadOnlyList<DropdownOption> options)
        {
            foreach (var option in options)
            {
                // Selected-aware hover mirrors the prompt ModelPicker rows.
                option.Button.PointerEntered += (_, _) => option.Button.Background = IsOptionSelected(options, option)
                    ? (Brush)Application.Current.Resources["RuLineBrush"]
                    : (Brush)Application.Current.Resources["RuCanvasBrush"];
                option.Button.PointerExited += (_, _) => option.Button.Background = IsOptionSelected(options, option)
                    ? (Brush)Application.Current.Resources["RuAccentSoftBrush"]
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                option.Button.PointerCanceled += (_, _) => option.Button.Background = IsOptionSelected(options, option)
                    ? (Brush)Application.Current.Resources["RuAccentSoftBrush"]
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                option.Button.PointerCaptureLost += (_, _) => option.Button.Background = IsOptionSelected(options, option)
                    ? (Brush)Application.Current.Resources["RuAccentSoftBrush"]
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        }

        private static bool IsOptionSelected(IReadOnlyList<DropdownOption> options, DropdownOption option) =>
            option.Check.Visibility == Visibility.Visible;

        public SettingsView()
        {
            this.InitializeComponent();
            InitDropdownOptions();
            InitializeSettingsHoverEffects();
        }

        public void Initialize(ApiService apiService, ProviderCatalogController? providerCatalogController = null)
        {
            DisposeEventHandlers();
            _apiService = apiService;
            _providerCatalogController = providerCatalogController ?? new ProviderCatalogController(new ProviderCatalogService(apiService));
            _ownsProviderCatalogController = providerCatalogController is null;
            _ = LoadSettingsAsync();
            SetupAutoSave();
        }

        private void SetupAutoSave()
        {
            _autoSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            _autoSaveTimer.Tick += OnAutoSaveTimerTick;

            OpenCodeApiKeyBox.PasswordChanged += AutoSaveHandler;
            KiloApiKeyBox.PasswordChanged += AutoSaveHandler;
            OpenAiApiKeyBox.PasswordChanged += AutoSaveHandler;
            AnthropicApiKeyBox.PasswordChanged += AutoSaveHandler;
            CustomEndpointUrlBox.TextChanged += AutoSaveTextHandler;
            CustomApiKeyBox.PasswordChanged += AutoSaveHandler;
        }

        private async void OnAutoSaveTimerTick(object? sender, object e)
        {
            if (_autoSaveTimer != null)
            {
                _autoSaveTimer.Stop();
            }
            await SaveSettingsAsync();
        }

        private void ScheduleAutoSave()
        {
            if (_loading) return;
            _autoSaveTimer?.Stop();
            _autoSaveTimer?.Start();
        }

        private async Task LoadSettingsAsync()
        {
            _loading = true;
            if (_apiService != null)
            {
                var settings = await _apiService.GetSettingsAsync();
                settings ??= new AppSettingsDto();
                SetCurrentProvider(settings.BackendChoice ?? "opencode");

                OpenCodeApiKeyBox.Password = string.Empty;
                OpenCodeApiKeyBox.PlaceholderText = settings.HasOpenCodeKey
                    ? "Saved (leave blank to keep unchanged)"
                    : "sk-opencode-...";
                KiloApiKeyBox.Password = string.Empty;
                KiloApiKeyBox.PlaceholderText = settings.HasKiloKey
                    ? "Saved (leave blank to keep unchanged)"
                    : "sk-kilo-...";
                OpenAiApiKeyBox.Password = string.Empty;
                OpenAiApiKeyBox.PlaceholderText = settings.HasOpenAiKey
                    ? "Saved (leave blank to keep unchanged)"
                    : "sk-...";
                AnthropicApiKeyBox.Password = string.Empty;
                AnthropicApiKeyBox.PlaceholderText = settings.HasAnthropicKey
                    ? "Saved (leave blank to keep unchanged)"
                    : "sk-ant-...";
                CustomEndpointUrlBox.Text = settings.CustomUrl;
                CustomApiKeyBox.Password = string.Empty;
                CustomApiKeyBox.PlaceholderText = settings.HasCustomKey
                    ? "Saved (leave blank to keep unchanged)"
                    : "sk-...";


                {
                }

                UpdateVisibilityForBackend(_providerChoice);
                _providerCatalogController?.SetBackend(_providerChoice);
            }
            _loading = false;
        }

        private void UpdateVisibilityForBackend(string choice)
        {
            OpenCodeApiRow.Visibility = (choice == "opencode") ? Visibility.Visible : Visibility.Collapsed;
            KiloApiRow.Visibility = (choice == "kilo") ? Visibility.Visible : Visibility.Collapsed;
            OpenAiApiRow.Visibility = (choice == "openai") ? Visibility.Visible : Visibility.Collapsed;
            AnthropicApiRow.Visibility = (choice == "anthropic") ? Visibility.Visible : Visibility.Collapsed;
            CustomEndpointRow.Visibility = (choice == "custom") ? Visibility.Visible : Visibility.Collapsed;
            CustomApiKeyRow.Visibility = (choice == "custom") ? Visibility.Visible : Visibility.Collapsed;

        }

        private void OnProviderOptionClick(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (sender is not Button button || button.Tag is not string choice) return;
            if (!KnownProviders.Contains(choice)) return;
            _providerChoice = choice;
            RefreshProviderUI();
            (ProviderButton.Flyout as Flyout)?.Hide();

            UpdateVisibilityForBackend(choice);
            _providerCatalogController?.SetBackend(choice);
            _ = _providerCatalogController?.RefreshAsync(force: true);
            ScheduleAutoSave();
        }

        public async Task RefreshProviderCatalogAsync()
        {
            if (_providerCatalogController is not null)
            {
                await _providerCatalogController.RefreshAsync(force: true);
            }
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
                string backend = _providerChoice;

                var dto = new ProviderSettingsUpdateDto
                {
                    BackendChoice = backend,
                    OpenCodeApiKey = OpenCodeApiKeyBox.Password,
                    KiloApiKey = KiloApiKeyBox.Password,
                    OpenAiApiKey = OpenAiApiKeyBox.Password,
                    AnthropicApiKey = AnthropicApiKeyBox.Password,
                    CustomUrl = CustomEndpointUrlBox.Text?.Trim() ?? "http://localhost:8000/v1",
                    CustomApiKey = CustomApiKeyBox.Password,
                };

                bool ok = await _apiService.SaveProviderSettingsAsync(dto);
                if (!ok)
                {
                    System.Diagnostics.Debug.WriteLine("[Settings] Auto-save failed");
                }
            }
            catch (Exception ex)
            {
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

        private async void OnRemoveProviderClick(object sender, RoutedEventArgs e)
        {
            if (_apiService is null)
            {
                return;
            }

            if (sender is not Button button
                || button.Tag is not string alias
                || string.IsNullOrWhiteSpace(alias))
            {
                return;
            }

            button.IsEnabled = false;
            try
            {
                bool ok = await _apiService.RemoveProviderAsync(alias);
                if (ok)
                {
                    ClearProviderKeyInput(alias);
                    await LoadSettingsAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Sign out error: {ex.Message}");
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        private void ClearProviderKeyInput(string alias)
        {
            switch (alias)
            {
                case "opencode":
                    OpenCodeApiKeyBox.Password = string.Empty;
                    break;
                case "kilo":
                    KiloApiKeyBox.Password = string.Empty;
                    break;
                case "openai":
                    OpenAiApiKeyBox.Password = string.Empty;
                    break;
                case "anthropic":
                    AnthropicApiKeyBox.Password = string.Empty;
                    break;
                case "custom":
                    CustomApiKeyBox.Password = string.Empty;
                    break;
            }
        }

        public void Dispose()
        {
            DisposeEventHandlers();
            if (_providerCatalogController is not null)
            {
                if (_ownsProviderCatalogController)
                {
                    _ = _providerCatalogController.DisposeAsync().AsTask();
                }
                _providerCatalogController = null;
                _ownsProviderCatalogController = false;
            }
            _apiService = null;
        }

        private void DisposeEventHandlers()
        {
            OpenCodeApiKeyBox.PasswordChanged -= AutoSaveHandler;
            KiloApiKeyBox.PasswordChanged -= AutoSaveHandler;
            OpenAiApiKeyBox.PasswordChanged -= AutoSaveHandler;
            AnthropicApiKeyBox.PasswordChanged -= AutoSaveHandler;
            CustomEndpointUrlBox.TextChanged -= AutoSaveTextHandler;
            CustomApiKeyBox.PasswordChanged -= AutoSaveHandler;
            if (_autoSaveTimer != null)
            {
                _autoSaveTimer.Tick -= OnAutoSaveTimerTick;
                _autoSaveTimer.Stop();
                _autoSaveTimer = null;
            }
        }

        private void AutoSaveHandler(object sender, RoutedEventArgs e) => ScheduleAutoSave();

        private void AutoSaveTextHandler(object sender, TextChangedEventArgs e) => ScheduleAutoSave();

        private void OnThemeOptionClick(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (sender is not Button button || button.Tag is not string tag) return;
            _themeChoice = string.Equals(tag, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
            RefreshThemeUI();
            (ThemeButton.Flyout as Flyout)?.Hide();
            var theme = _themeChoice == "light" ? UiTheme.Light : UiTheme.Dark;
            ThemeChanged?.Invoke(theme);
        }

        private void OnPresetOptionClick(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (sender is not Button button || button.Tag is not string tag) return;
            _presetChoice = string.Equals(tag, "trusted", StringComparison.OrdinalIgnoreCase) ? "trusted" : "standard";
            RefreshPresetUI();
            (PresetButton.Flyout as Flyout)?.Hide();
            var preset = _presetChoice == "trusted" ? AgentPermissionPreset.Trusted : AgentPermissionPreset.Standard;
            PresetChanged?.Invoke(preset);
        }

        private void InitializeSettingsHoverEffects()
        {
            SelectTab(isGeneral: true);
        }

        private void OnNavGeneralClick(object sender, RoutedEventArgs e)
        {
            SelectTab(isGeneral: true);
        }

        private void OnNavModelsClick(object sender, RoutedEventArgs e)
        {
            SelectTab(isGeneral: false);
            _ = _providerCatalogController?.RefreshAsync(force: true);
        }

        private void SelectTab(bool isGeneral)
        {
            if (PanelGeneralScrollViewer != null)
            {
                PanelGeneralScrollViewer.Visibility = isGeneral ? Visibility.Visible : Visibility.Collapsed;
            }
            if (PanelModelsScrollViewer != null)
            {
                PanelModelsScrollViewer.Visibility = isGeneral ? Visibility.Collapsed : Visibility.Visible;
            }

            if (isGeneral)
            {
                if (CategoryTitleText != null) CategoryTitleText.Text = "General";
                if (CategorySubtitleText != null) CategorySubtitleText.Text = "Manage application theme and agent defaults.";
                HighlightNavButton(NavGeneralBtn, NavModelsBtn);
            }
            else
            {
                if (CategoryTitleText != null) CategoryTitleText.Text = "Models & Providers";
                if (CategorySubtitleText != null) CategorySubtitleText.Text = "Configure cloud AI model providers, API credentials, and server connections.";
                HighlightNavButton(NavModelsBtn, NavGeneralBtn);
            }
        }

        private void HighlightNavButton(Button? active, Button? inactive)
        {
            if (active != null)
            {
                active.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuLineBrush"];
                active.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuInkBrush"];
                active.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }
            if (inactive != null)
            {
                inactive.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                inactive.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuMutedBrush"];
                inactive.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            }
        }
    }
}
