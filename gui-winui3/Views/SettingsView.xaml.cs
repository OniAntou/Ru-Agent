using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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

        /// <summary>
        /// The stored conversation text size, read once at construction so the picker
        /// opens on the size the transcript is already using.
        /// </summary>
        private double _transcriptSize = TranscriptTypography.Instance.MessageFontSize;

        private sealed record DropdownOption(Button Button, TextBlock Label, FontIcon Check, string Value);
        private IReadOnlyList<DropdownOption>? _themeOptions;
        private IReadOnlyList<DropdownOption>? _presetOptions;
        private IReadOnlyList<DropdownOption>? _providerOptions;

        /// <summary>
        /// Models.dev rows appended to the provider dropdown at runtime, plus
        /// their display names by lowercase id.
        /// </summary>
        private readonly List<DropdownOption> _modelsDevOptions = new();
        private readonly Dictionary<string, string> _modelsDevNames = new(StringComparer.Ordinal);

        public void SetCurrentTheme(UiTheme theme)
        {
            _themeChoice = theme == UiTheme.Light ? "light" : "dark";
            RefreshThemeUI();
        }

        public void SetCurrentPreset(AgentPermissionPreset preset)
        {
            _presetChoice = preset switch
            {
                AgentPermissionPreset.Plan => "plan",
                AgentPermissionPreset.FullAccess => "full_access",
                _ => "standard",
            };
            RefreshPresetUI();
        }

        public void SetCurrentProvider(string backendChoice)
        {
            _providerChoice = ProviderSetupGuidance.NormalizeProviderChoice(backendChoice);
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
            RefreshDropdownUI(
                _providerOptions.Concat(_modelsDevOptions).ToArray(),
                _providerChoice,
                ProviderButtonLabel);
            if (ProviderSetupGuidance.IsModelsDevChoice(_providerChoice))
            {
                // Before the runtime rows load there is nothing to match, so
                // name the trigger from the fetched list (or the raw id).
                var id = _providerChoice[ProviderSetupGuidance.ModelsDevPrefix.Length..];
                ProviderButtonLabel.Text = _modelsDevNames.TryGetValue(id, out var name) ? name : id;
            }
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
                new DropdownOption(PresetPlanOption, PresetPlanText, PresetPlanCheck, "plan"),
                new DropdownOption(PresetStandardOption, PresetStandardText, PresetStandardCheck, "standard"),
                new DropdownOption(PresetFullAccessOption, PresetFullAccessText, PresetFullAccessCheck, "full_access"),
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

        /// <summary>
        /// Builds the conversation text size rows and selects the stored size.
        /// </summary>
        /// <remarks>
        /// Rows are created rather than declared because the range belongs to
        /// <see cref="TranscriptFontSizeStore"/>: a declared list would be a second
        /// copy of it, and the two could disagree.
        /// </remarks>
        private void InitTranscriptSizeOptions()
        {
            if (TranscriptSizeOptionsPanel == null || TranscriptSizeButtonLabel == null)
            {
                return;
            }

            TranscriptSizeOptionsPanel.Children.Clear();
            foreach (var size in TranscriptSizePickerProjection.Sizes())
            {
                var row = new RadioButton
                {
                    Content = TranscriptSizePickerProjection.LabelWithActivity(size),
                    Tag = size,
                    GroupName = "TranscriptFontSize",
                    MinHeight = 32,
                    IsChecked = Math.Abs(size - _transcriptSize) < 0.001,
                };
                AutomationProperties.SetAutomationId(
                    row,
                    TranscriptSizePickerProjection.OptionAutomationId(size));
                row.Checked += OnTranscriptSizeOptionChecked;
                TranscriptSizeOptionsPanel.Children.Add(row);
            }

            TranscriptSizeButtonLabel.Text = TranscriptSizePickerProjection.Label(_transcriptSize);
        }

        private void OnTranscriptSizeOptionChecked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { Tag: double size })
            {
                return;
            }

            TranscriptTypography.Instance.MessageFontSize = size;
            _transcriptSize = TranscriptTypography.Instance.MessageFontSize;
            TranscriptSizeButtonLabel.Text = TranscriptSizePickerProjection.Label(_transcriptSize);
        }

        /// <summary>
        /// Asks the selected provider whether it answers, and reports what it said.
        /// </summary>
        /// <remarks>
        /// The pending edit is saved first. The backend answers /models from stored
        /// settings, and auto-save is debounced by 800 ms, so testing straight after
        /// typing a key would otherwise prove the previous key.
        /// </remarks>
        private async void OnProviderTestClick(object sender, RoutedEventArgs e)
        {
            if (_apiService is null || _providerCatalogController is null)
            {
                ShowProviderTestResult(new ProviderConnectionTest(
                    ProviderConnectionTest.ErrorTone,
                    "The backend is not ready yet. Try again in a moment."));
                return;
            }

            _autoSaveTimer?.Stop();
            ProviderTestButton.IsEnabled = false;
            ShowProviderTestResult(ProviderConnectionTestPresentation.Testing());

            try
            {
                await SaveSettingsAsync();

                // A forced refresh is what actually re-queries the provider.
                await _providerCatalogController.RefreshAsync(force: true);
                ShowProviderTestResult(ProviderConnectionTestPresentation.Project(
                    _providerCatalogController.State));
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.WriteCrashEvent(ex);
                ShowProviderTestResult(new ProviderConnectionTest(
                    ProviderConnectionTest.ErrorTone,
                    "The test could not run. Open Tools → Activity Log to check."));
            }
            finally
            {
                ProviderTestButton.IsEnabled = true;
            }
        }

        private void ShowProviderTestResult(ProviderConnectionTest result)
        {
            if (ProviderTestStatusText is null)
            {
                return;
            }

            ProviderTestStatusText.Text = result.Message;
            ProviderTestStatusText.Foreground = (Brush)Application.Current.Resources[
                result.Tone switch
                {
                    ProviderConnectionTest.SuccessTone => "AppSuccessBrush",
                    ProviderConnectionTest.ErrorTone => "AppDangerBrush",
                    _ => "RuMutedBrush",
                }];
        }

        /// <summary>
        /// Reflects a background catalog failure in the status line, but never a
        /// background success: the catalog also refreshes on a timer, and a result
        /// appearing without the button being pressed would read as the user's own.
        /// </summary>
        private void OnProviderCatalogStateChanged(object? sender, ProviderCatalogState state)
        {
            if (ProviderConnectionTestPresentation.ReportsFailure(
                    ProviderConnectionTestPresentation.Project(state)))
            {
                ShowProviderTestResult(ProviderConnectionTestPresentation.Project(state));
            }
        }

        private static void AttachDropdownHover(IReadOnlyList<DropdownOption> options)
        {            foreach (var option in options)
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
            InitTranscriptSizeOptions();
            InitializeSettingsHoverEffects();
        }

        public void Initialize(ApiService apiService, ProviderCatalogController? providerCatalogController = null)
        {
            DisposeEventHandlers();
            _apiService = apiService;
            _providerCatalogController = providerCatalogController ?? new ProviderCatalogController(new ProviderCatalogService(apiService));
            _ownsProviderCatalogController = providerCatalogController is null;
            _providerCatalogController.StateChanged += OnProviderCatalogStateChanged;
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
                ModelsDevApiKeyBox.Password = string.Empty;
                ModelsDevKeyStatus.Text = string.Empty;
                // Providers first: the current choice can only be named once
                // the runtime rows exist.
                await RefreshModelsDevProvidersAsync();
                SetCurrentProvider(settings.BackendChoice ?? "opencode");

                {
                }

                UpdateVisibilityForBackend(_providerChoice);
                _providerCatalogController?.SetBackend(_providerChoice);
            }
            _loading = false;
        }

        /// <summary>
        /// Lists Models.dev providers for the key row and appends them to the
        /// Active Provider dropdown. Best-effort: an empty list keeps the key
        /// row usable for a manually typed provider id.
        /// </summary>
        private async Task RefreshModelsDevProvidersAsync()
        {
            if (_apiService is null) return;
            try
            {
                var providers = await _apiService.GetModelsDevAsync();
                var selected = ModelsDevProviderBox.SelectedItem as string;
                ModelsDevProviderBox.ItemsSource = providers.Select(entry => entry.Id).ToArray();
                if (selected is not null && providers.Any(entry => entry.Id == selected))
                {
                    ModelsDevProviderBox.SelectedItem = selected;
                }

                RebuildModelsDevOptions(providers);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Error loading Models.dev providers: {ex.Message}");
            }
        }

        /// <summary>
        /// Appends one dropdown row per Models.dev provider after the fixed
        /// five, reusing the same option shape so selection, check mark, and
        /// trigger label behave identically.
        /// </summary>
        private void RebuildModelsDevOptions(IReadOnlyList<ModelsDevEntryDto> providers)
        {
            _modelsDevOptions.Clear();
            _modelsDevNames.Clear();
            if (ModelsDevOptionsPanel is not null)
            {
                ModelsDevOptionsPanel.Children.Clear();
            }

            if (ModelsDevOptionsPanel is not null && providers.Count > 0
                && Application.Current.Resources["RuDropdownItemButtonStyle"] is Style itemStyle)
            {
                var uiFont = Application.Current.Resources["RuUiFontFamily"];
                var ink = Application.Current.Resources["RuInkBrush"];
                var accent = Application.Current.Resources["RuAccentBrush"];
                foreach (var entry in providers)
                {
                    var id = (entry.Id ?? string.Empty).Trim().ToLowerInvariant();
                    if (id.Length == 0 || _modelsDevNames.ContainsKey(id))
                    {
                        continue;
                    }

                    var name = string.IsNullOrWhiteSpace(entry.Name) ? id : entry.Name.Trim();
                    _modelsDevNames[id] = name;

                    var label = new TextBlock
                    {
                        Text = name,
                        FontFamily = uiFont as Microsoft.UI.Xaml.Media.FontFamily,
                        FontSize = 12.5,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    if (ink is Microsoft.UI.Xaml.Media.Brush inkBrush)
                    {
                        label.Foreground = inkBrush;
                    }

                    var check = new FontIcon
                    {
                        Glyph = "\uE73E",
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0),
                        Visibility = Visibility.Collapsed,
                    };
                    if (accent is Microsoft.UI.Xaml.Media.Brush accentBrush)
                    {
                        check.Foreground = accentBrush;
                    }

                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.Children.Add(label);
                    grid.Children.Add(check);
                    Grid.SetColumn(check, 1);

                    var value = ProviderSetupGuidance.ModelsDevPrefix + id;
                    var button = new Button
                    {
                        Tag = value,
                        Style = itemStyle,
                        Content = grid,
                    };
                    AutomationProperties.SetName(button, $"{name} provider");
                    button.Click += OnProviderOptionClick;
                    ModelsDevOptionsPanel.Children.Add(button);
                    _modelsDevOptions.Add(new DropdownOption(button, label, check, value));
                }
            }

            if (ModelsDevOptionsDivider is not null)
            {
                ModelsDevOptionsDivider.Visibility =
                    _modelsDevOptions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            SyncModelsDevKeyBox();
            RefreshProviderUI();
        }

        /// <summary>
        /// Points the key row's provider box at the active Models.dev choice,
        /// so saving a key needs no second selection step.
        /// </summary>
        private void SyncModelsDevKeyBox()
        {
            if (ModelsDevProviderBox is null) return;
            if (ProviderSetupGuidance.IsModelsDevChoice(_providerChoice))
            {
                var id = _providerChoice[ProviderSetupGuidance.ModelsDevPrefix.Length..];
                if (_modelsDevNames.ContainsKey(id))
                {
                    ModelsDevProviderBox.SelectedItem = id;
                    return;
                }
            }
        }

        private async void OnSaveModelsDevKeyClick(object sender, RoutedEventArgs e)
        {
            if (_apiService is null) return;
            var providerId = (ModelsDevProviderBox.SelectedItem as string)?.Trim().ToLowerInvariant();
            var apiKey = ModelsDevApiKeyBox.Password;
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(apiKey))
            {
                ModelsDevKeyStatus.Text = "Chọn provider và dán key trước khi lưu.";
                return;
            }

            ModelsDevSaveButton.IsEnabled = false;
            try
            {
                bool ok = await _apiService.SaveModelsDevKeyAsync(providerId, apiKey);
                ModelsDevKeyStatus.Text = ok ? $"Đã lưu key cho {providerId}." : "Lưu thất bại, thử lại.";
                if (ok)
                {
                    ModelsDevApiKeyBox.Password = string.Empty;
                    await LoadSettingsAsync();
                }
            }
            finally
            {
                ModelsDevSaveButton.IsEnabled = true;
            }
        }

        private void UpdateVisibilityForBackend(string choice)
        {
            bool isModelsDev = ProviderSetupGuidance.IsModelsDevChoice(choice);
            OpenCodeApiRow.Visibility = (!isModelsDev && choice == "opencode") ? Visibility.Visible : Visibility.Collapsed;
            KiloApiRow.Visibility = (!isModelsDev && choice == "kilo") ? Visibility.Visible : Visibility.Collapsed;
            OpenAiApiRow.Visibility = (!isModelsDev && choice == "openai") ? Visibility.Visible : Visibility.Collapsed;
            AnthropicApiRow.Visibility = (!isModelsDev && choice == "anthropic") ? Visibility.Visible : Visibility.Collapsed;
            CustomEndpointRow.Visibility = (!isModelsDev && choice == "custom") ? Visibility.Visible : Visibility.Collapsed;
            CustomApiKeyRow.Visibility = (!isModelsDev && choice == "custom") ? Visibility.Visible : Visibility.Collapsed;
            if (ModelsDevApiKeyRow is not null)
            {
                ModelsDevApiKeyRow.Visibility = isModelsDev ? Visibility.Visible : Visibility.Collapsed;
            }

            if (isModelsDev)
            {
                SyncModelsDevKeyBox();
            }
        }

        private void OnProviderOptionClick(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (sender is not Button button || button.Tag is not string choice) return;
            if (!ProviderSetupGuidance.IsSelectableProvider(choice)) return;
            _providerChoice = ProviderSetupGuidance.NormalizeProviderChoice(choice);
            RefreshProviderUI();
            (ProviderButton.Flyout as Flyout)?.Hide();

            UpdateVisibilityForBackend(_providerChoice);
            _providerCatalogController?.SetBackend(_providerChoice);
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
            if (_providerCatalogController is not null)
            {
                _providerCatalogController.StateChanged -= OnProviderCatalogStateChanged;
            }
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
            if (string.Equals(tag, "plan", StringComparison.OrdinalIgnoreCase))
            {
                _presetChoice = "plan";
            }
            else if (string.Equals(tag, "full_access", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "full", StringComparison.OrdinalIgnoreCase))
            {
                _presetChoice = "full_access";
            }
            else
            {
                _presetChoice = "standard";
            }
            RefreshPresetUI();
            (PresetButton.Flyout as Flyout)?.Hide();
            var preset = _presetChoice switch
            {
                "plan" => AgentPermissionPreset.Plan,
                "full_access" => AgentPermissionPreset.FullAccess,
                _ => AgentPermissionPreset.Standard,
            };
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

        /// <summary>
        /// Opens the provider section from outside the settings surface, for a
        /// failure message whose fix lives here.
        /// </summary>
        /// <remarks>
        /// Deliberately the same path as clicking the Models tab: a deep link that
        /// skipped the catalog refresh would show stale providers.
        /// </remarks>
        public void ShowProviders()
        {
            OnNavModelsClick(this, new RoutedEventArgs());
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
