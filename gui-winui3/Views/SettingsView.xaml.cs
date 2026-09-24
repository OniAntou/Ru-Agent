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
        private string _providerChoice = "openai";

        /// <summary>
        /// The stored conversation text size, read once at construction so the picker
        /// opens on the size the transcript is already using.
        /// </summary>
        private double _transcriptSize = TranscriptTypography.Instance.MessageFontSize;

        private sealed record DropdownOption(Button Button, TextBlock Label, FontIcon Check, string Value, FrameworkElement? KeyDot = null);

        private const double ProviderRowDotSize = 6;
        private const double ProviderRowGap = 8;
        private const double ProviderRowLabelFontSize = 12.5;
        private const double ProviderRowCheckFontSize = 12;

        private IReadOnlyList<DropdownOption>? _themeOptions;
        private IReadOnlyList<DropdownOption>? _presetOptions;
        private IReadOnlyList<DropdownOption>? _providerOptions;

        /// <summary>
        /// Models.dev rows appended to the provider dropdown at runtime, plus
        /// their display names by lowercase id.
        /// </summary>
        private readonly List<DropdownOption> _modelsDevOptions = new();
        private readonly Dictionary<string, string> _modelsDevNames = new(StringComparer.Ordinal);

        /// <summary>
        /// Signed-in duplicates pinned above the groups, plus the choice
        /// values with a stored key and the last settings/keys snapshots
        /// they were built from.
        /// </summary>
        private readonly List<DropdownOption> _signedInOptions = new();
        private readonly HashSet<string> _signedInValues = new(StringComparer.OrdinalIgnoreCase);
        private AppSettingsDto? _lastSettings;
        private IReadOnlyList<string> _modelsDevKeyIds = [];
        private bool _modelsDevLoadFailed;

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
                _providerOptions.Concat(_modelsDevOptions).Concat(_signedInOptions).ToArray(),
                _providerChoice,
                ProviderButtonLabel);
            RefreshSignedInDots();
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
                new DropdownOption(ProviderOpenaiOption, ProviderOpenaiText, ProviderOpenaiCheck, "openai", ProviderOpenaiDot),
                new DropdownOption(ProviderAnthropicOption, ProviderAnthropicText, ProviderAnthropicCheck, "anthropic", ProviderAnthropicDot),
                new DropdownOption(ProviderCustomOption, ProviderCustomText, ProviderCustomCheck, "custom", ProviderCustomDot),
            };
            // Row hover is owned by RuDropdownItemButtonStyle (VisualStates), so
            // every row — including runtime-built Models.dev rows — reacts
            // alike with no per-row handlers.
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

            OpenAiApiKeyBox.PasswordChanged += AutoSaveHandler;
            AnthropicApiKeyBox.PasswordChanged += AutoSaveHandler;
            CustomEndpointUrlBox.TextChanged += AutoSaveTextHandler;
            CustomApiKeyBox.PasswordChanged += AutoSaveHandler;
            CloudflareAccountIdBox.TextChanged += AutoSaveTextHandler;
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
                _lastSettings = settings;

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
                SetCurrentProvider(settings.BackendChoice ?? "openai");

                {
                }

                UpdateVisibilityForBackend(_providerChoice);
                _providerCatalogController?.SetBackend(_providerChoice);
            }
            _loading = false;
        }

        /// <summary>
        /// Appends Models.dev providers to the Active Provider dropdown.
        /// The key row saves for the active provider, so it needs no
        /// provider picker of its own.
        /// </summary>
        private async Task RefreshModelsDevProvidersAsync()
        {
            if (_apiService is null) return;
            try
            {
                var providers = await _apiService.GetModelsDevAsync();
                _modelsDevLoadFailed = providers.Count == 0;
                RebuildModelsDevOptions(providers);
                _modelsDevKeyIds = await _apiService.GetModelsDevKeysAsync();
                RebuildSignedInOptions(providers);
            }
            catch (Exception ex)
            {
                _modelsDevLoadFailed = true;
                System.Diagnostics.Debug.WriteLine($"[Settings] Error loading Models.dev providers: {ex.Message}");
                ApplyProviderFilter(ProviderSearchBox?.Text);
            }
        }

        /// <summary>
        /// Builds one dropdown row: status dot, name label, check mark.
        /// Shared by the Models.dev group and the signed-in duplicates so
        /// both instances of a provider look and behave identically.
        /// </summary>
        private DropdownOption BuildProviderRowButton(string value, string name, bool showDot)
        {
            var uiFont = Application.Current.Resources["RuUiFontFamily"];
            var ink = Application.Current.Resources["RuInkBrush"];
            var accent = Application.Current.Resources["RuAccentBrush"];
            var success = Application.Current.Resources["RuSuccessBrush"];
            var itemStyle = (Style)Application.Current.Resources["RuDropdownItemButtonStyle"];

            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = ProviderRowDotSize,
                Height = ProviderRowDotSize,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, ProviderRowGap, 0),
                Visibility = showDot ? Visibility.Visible : Visibility.Collapsed,
            };
            if (success is Microsoft.UI.Xaml.Media.Brush successBrush)
            {
                dot.Fill = successBrush;
            }

            var label = new TextBlock
            {
                Text = name,
                FontFamily = uiFont as Microsoft.UI.Xaml.Media.FontFamily,
                FontSize = ProviderRowLabelFontSize,
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
                FontSize = ProviderRowCheckFontSize,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(ProviderRowGap, 0, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            if (accent is Microsoft.UI.Xaml.Media.Brush accentBrush)
            {
                check.Foreground = accentBrush;
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(dot);
            grid.Children.Add(label);
            grid.Children.Add(check);
            Grid.SetColumn(label, 1);
            Grid.SetColumn(check, 2);

            var button = new Button
            {
                Tag = value,
                Style = itemStyle,
                Content = grid,
            };
            AutomationProperties.SetName(button, $"{name} provider");
            button.Click += OnProviderOptionClick;
            return new DropdownOption(button, label, check, value, dot);
        }

        /// <summary>
        /// Appends one dropdown row per Models.dev provider, then merges the
        /// three fixed rows into the same panel so the whole list sorts A-Z.
        /// </summary>
        private void RebuildModelsDevOptions(IReadOnlyList<ModelsDevEntryDto> providers)
        {
            _modelsDevNames.Clear();
            if (ModelsDevOptionsPanel is not null)
            {
                foreach (var old in _modelsDevOptions)
                {
                    ModelsDevOptionsPanel.Children.Remove(old.Button);
                }
            }

            _modelsDevOptions.Clear();
            if (ModelsDevOptionsPanel is not null && providers.Count > 0)
            {
                var ordered = ProviderSetupGuidance.OrderModelsDevOptions(
                    providers.Select(entry => (entry.Id, entry.Name)));
                foreach (var (id, name) in ordered)
                {
                    _modelsDevNames[id] = name;
                    var value = ProviderSetupGuidance.ModelsDevPrefix + id;
                    var option = BuildProviderRowButton(value, name, showDot: false);
                    ModelsDevOptionsPanel.Children.Add(option.Button);
                    _modelsDevOptions.Add(option);
                }
            }

            MergeAndSortProviderRows();
            ApplyProviderFilter(ProviderSearchBox?.Text);
        }

        /// <summary>
        /// Pulls the fixed rows into the shared panel and sorts every row
        /// A-Z by display name, so built-in and Models.dev providers read as
        /// one list. Runs on every rebuild; moving an already-moved button
        /// is a no-op and the sort keeps the order stable.
        /// </summary>
        private void MergeAndSortProviderRows()
        {
            if (ModelsDevOptionsPanel is null || _providerOptions is null) return;
            var panel = ModelsDevOptionsPanel;
            foreach (var option in _providerOptions)
            {
                if (option.Button.Parent is Panel oldParent && !ReferenceEquals(oldParent, panel))
                {
                    oldParent.Children.Remove(option.Button);
                    panel.Children.Add(option.Button);
                }
            }

            var sorted = _providerOptions.Concat(_modelsDevOptions)
                .OrderBy(option => option.Label.Text, StringComparer.OrdinalIgnoreCase)
                .ThenBy(option => option.Value, StringComparer.OrdinalIgnoreCase)
                .Select(option => option.Button)
                .ToList();
            panel.Children.Clear();
            foreach (var button in sorted)
            {
                panel.Children.Add(button);
            }
        }

        /// <summary>
        /// Pins one duplicate row per signed-in provider above the groups.
        /// Dots everywhere reflect the same key set, refreshed here.
        /// </summary>
        private void RebuildSignedInOptions(IReadOnlyList<ModelsDevEntryDto> providers)
        {
            _signedInOptions.Clear();
            _signedInValues.Clear();
            if (SignedInOptionsPanel is not null)
            {
                SignedInOptionsPanel.Children.Clear();
            }

            if (SignedInOptionsPanel is not null)
            {
                var fixedRows = new (string Value, string Name, bool HasKey)[]
                {
                    ("openai", ProviderOpenaiText?.Text ?? "OpenAI", _lastSettings?.HasOpenAiKey == true),
                    ("anthropic", ProviderAnthropicText?.Text ?? "Anthropic", _lastSettings?.HasAnthropicKey == true),
                    ("custom", ProviderCustomText?.Text ?? "Custom", _lastSettings?.HasCustomKey == true),
                };
                var ordered = ProviderSetupGuidance.OrderSignedInOptions(
                    fixedRows,
                    providers.Select(entry => (entry.Id, entry.Name)),
                    _modelsDevKeyIds);
                foreach (var (value, name) in ordered)
                {
                    _signedInValues.Add(value);
                    var option = BuildProviderRowButton(value, name, showDot: true);
                    SignedInOptionsPanel.Children.Add(option.Button);
                    _signedInOptions.Add(option);
                }
            }

            RefreshSignedInDots();
            ApplyProviderFilter(ProviderSearchBox?.Text);
        }

        /// <summary>
        /// Lights the key dot on every row whose provider has a stored key.
        /// </summary>
        private void RefreshSignedInDots()
        {
            if (_providerOptions is null) return;
            foreach (var option in _providerOptions.Concat(_modelsDevOptions).Concat(_signedInOptions))
            {
                if (option.KeyDot is not null)
                {
                    option.KeyDot.Visibility = _signedInValues.Contains(option.Value)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }
        }

        /// <summary>
        /// Filters all three provider groups by the search text and hides a
        /// group header when none of its rows match.
        /// </summary>
        private void OnProviderSearchChanged(object sender, TextChangedEventArgs e)
        {
            ApplyProviderFilter((sender as TextBox)?.Text);
        }

        private void ApplyProviderFilter(string? filter)
        {
            bool anyFixed = false;
            if (_providerOptions is not null)
            {
                foreach (var option in _providerOptions)
                {
                    bool show = ProviderSetupGuidance.MatchesProviderFilter(option.Label.Text, option.Value, filter);
                    option.Button.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    anyFixed |= show;
                }
            }

            bool anyDev = false;
            foreach (var option in _modelsDevOptions)
            {
                var id = option.Value[ProviderSetupGuidance.ModelsDevPrefix.Length..];
                bool show = ProviderSetupGuidance.MatchesProviderFilter(option.Label.Text, id, filter);
                option.Button.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                anyDev |= show;
            }

            bool anySignedIn = false;
            foreach (var option in _signedInOptions)
            {
                var id = ProviderSetupGuidance.IsModelsDevChoice(option.Value)
                    ? option.Value[ProviderSetupGuidance.ModelsDevPrefix.Length..]
                    : option.Value;
                bool show = ProviderSetupGuidance.MatchesProviderFilter(option.Label.Text, id, filter);
                option.Button.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                anySignedIn |= show;
            }

            var signedInVisibility = _signedInOptions.Count > 0 && anySignedIn ? Visibility.Visible : Visibility.Collapsed;
            if (SignedInOptionsHeader is not null)
            {
                SignedInOptionsHeader.Visibility = signedInVisibility;
            }

            if (FixedProvidersHeader is not null)
            {
                FixedProvidersHeader.Visibility = (anyFixed || anyDev) ? Visibility.Visible : Visibility.Collapsed;
                int totalProviders = (_providerOptions?.Count ?? 0) + _modelsDevOptions.Count;
                FixedProvidersHeader.Text = $"PROVIDERS ({totalProviders})";
            }

            if (ProviderNoMatchText is not null)
            {
                bool noMatch = !anyFixed && !anyDev && !anySignedIn;
                ProviderNoMatchText.Visibility = noMatch ? Visibility.Visible : Visibility.Collapsed;
                if (noMatch)
                {
                    ProviderNoMatchText.Text = _modelsDevLoadFailed && _modelsDevOptions.Count == 0
                        ? "Could not load the Models.dev list. Check the backend connection, then reopen Settings."
                        : "No provider matches.";
                }
            }

            RefreshProviderUI();
        }

        private async void OnSaveModelsDevKeyClick(object sender, RoutedEventArgs e)
        {
            if (_apiService is null) return;
            var providerId = ProviderSetupGuidance.IsModelsDevChoice(_providerChoice)
                ? _providerChoice[ProviderSetupGuidance.ModelsDevPrefix.Length..].Trim().ToLowerInvariant()
                : string.Empty;
            var apiKey = ModelsDevApiKeyBox.Password;
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(apiKey))
            {
                ModelsDevKeyStatus.Text = "Dán key trước khi lưu.";
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
            OpenAiApiRow.Visibility = (!isModelsDev && choice == "openai") ? Visibility.Visible : Visibility.Collapsed;
            AnthropicApiRow.Visibility = (!isModelsDev && choice == "anthropic") ? Visibility.Visible : Visibility.Collapsed;
            CustomEndpointRow.Visibility = (!isModelsDev && choice == "custom") ? Visibility.Visible : Visibility.Collapsed;
            CustomApiKeyRow.Visibility = (!isModelsDev && choice == "custom") ? Visibility.Visible : Visibility.Collapsed;
            if (ModelsDevApiKeyRow is not null)
            {
                ModelsDevApiKeyRow.Visibility = isModelsDev ? Visibility.Visible : Visibility.Collapsed;
            }

            if (CloudflareAccountIdBox is not null)
            {
                CloudflareAccountIdBox.Visibility = IsCloudflareChoice(choice) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private bool IsCloudflareChoice(string choice) =>
            string.Equals(
                ProviderSetupGuidance.NormalizeProviderChoice(choice),
                "models.dev:cloudflare-workers-ai",
                StringComparison.OrdinalIgnoreCase);

        private void OnProviderOptionClick(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (sender is not Button button || button.Tag is not string choice) return;
            if (!ProviderSetupGuidance.IsSelectableProvider(choice)) return;
            _providerChoice = ProviderSetupGuidance.NormalizeProviderChoice(choice);
            if (ProviderSearchBox is not null)
            {
                ProviderSearchBox.Text = string.Empty;
            }

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
                    OpenAiApiKey = OpenAiApiKeyBox.Password,
                    AnthropicApiKey = AnthropicApiKeyBox.Password,
                    CustomUrl = CustomEndpointUrlBox.Text?.Trim() ?? "http://localhost:8000/v1",
                    CustomApiKey = CustomApiKeyBox.Password,
                    CloudflareAccountId = CloudflareAccountIdBox.Text?.Trim() ?? string.Empty,
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
                    // Same reasoning as ChatView.Dispose: async disposal must
                    // not block the UI thread's Dispose.
                    var controller = _providerCatalogController;
                    _ = controller.DisposeAsync().AsTask().ContinueWith(
                        static faulted => System.Diagnostics.Debug.WriteLine(
                            $"[SettingsView] Catalog dispose failed: {faulted!.Exception!.GetBaseException().Message}"),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                }
                _providerCatalogController = null;
                _ownsProviderCatalogController = false;
            }
            _apiService = null;
        }

        private void DisposeEventHandlers()
        {
            OpenAiApiKeyBox.PasswordChanged -= AutoSaveHandler;
            AnthropicApiKeyBox.PasswordChanged -= AutoSaveHandler;
            CustomEndpointUrlBox.TextChanged -= AutoSaveTextHandler;
            CustomApiKeyBox.PasswordChanged -= AutoSaveHandler;
            CloudflareAccountIdBox.TextChanged -= AutoSaveTextHandler;
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
