using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuAgent.Client;
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
        private DispatcherTimer? _modelsDevKeyTimer;
        private bool _modelsDevKeyProbeRunning;
        private bool _saveInProgress;
        private bool _saveRequestedDuringSave;

        public event Action<UiTheme>? ThemeChanged;
        public event Action<AgentPermissionPreset>? PresetChanged;

        private string _themeChoice = "dark";
        private string _presetChoice = "standard";
        private string _providerChoice = string.Empty;

        /// <summary>
        /// The stored conversation text size, read once at construction so the picker
        /// opens on the size the transcript is already using.
        /// </summary>
        private double _transcriptSize = TranscriptTypography.Instance.MessageFontSize;

        private sealed record DropdownOption(Button Button, TextBlock Label, FontIcon Check, string Value, FrameworkElement? KeyDot = null);

        private const double ProviderRowLabelFontSize = 13;
        private const double ProviderFlyoutBottomMargin = 24;
        // Six 32px rows: the list stays scannable without eating the settings form.
        private const double ProviderFlyoutMaxHeight = 200;
        private const double ProviderFlyoutAbsoluteMinHeight = 96;

        private IReadOnlyList<DropdownOption>? _themeOptions;
        private IReadOnlyList<DropdownOption>? _presetOptions;

        /// <summary>
        /// The provider picker's rows, in display order. One entry per offerable
        /// provider: the custom endpoint plus the whole Models.dev catalog.
        /// </summary>
        private readonly ObservableCollection<ProviderRow> _providerRows = new();

        /// <summary>
        /// Models.dev display names by lowercase id, for naming the trigger before the
        /// rows exist and for resolving a stored choice to its label.
        /// </summary>
        private readonly Dictionary<string, string> _modelsDevNames = new(StringComparer.Ordinal);

        private AppSettingsDto? _lastSettings;
        private IReadOnlyList<string> _modelsDevKeyIds = [];
        private bool _modelsDevLoadFailed;

        public void SetCurrentTheme(UiTheme theme)
        {
            _themeChoice = theme switch
            {
                UiTheme.Dark => "dark",
                UiTheme.DarkMeteorShower => "dark-meteor",
                UiTheme.StarryNight => "starry-night",
                _ => "light",
            };
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
            // The stored choice may be unset or name a provider the app no longer
            // offers. Resolving that to a real provider needs the catalog, which is
            // fetched after settings; RebuildModelsDevOptions finishes the job once
            // it arrives. Until then this keeps the trigger label honest.
            _providerChoice = _modelsDevNames.Count > 0
                ? ProviderSetupGuidance.ResolveUnsetChoice(
                    backendChoice,
                    _modelsDevNames.Select(pair => (pair.Key, pair.Value)))
                : ProviderSetupGuidance.NormalizeProviderChoice(backendChoice);
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
            if (ProviderButtonLabel == null) return;

            foreach (var row in _providerRows)
            {
                row.IsSelected = string.Equals(row.Value, _providerChoice, StringComparison.OrdinalIgnoreCase);
                if (row.IsSelected)
                {
                    ProviderButtonLabel.Text = row.DisplayName;
                }
            }

            if (ProviderSetupGuidance.IsModelsDevChoice(_providerChoice))
            {
                // Before the runtime rows load there is nothing to match, so
                // name the trigger from the fetched list (or the raw id).
                var id = _providerChoice[ProviderSetupGuidance.ModelsDevPrefix.Length..];
                ProviderButtonLabel.Text = _modelsDevNames.TryGetValue(id, out var name) ? name : id;
            }
            else if (_providerChoice == ProviderSetupGuidance.UnsetChoice)
            {
                ProviderButtonLabel.Text = "No provider";
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
                new DropdownOption(ThemeDarkMeteorOption, ThemeDarkMeteorText, ThemeDarkMeteorCheck, "dark-meteor"),
                new DropdownOption(ThemeStarryNightOption, ThemeStarryNightText, ThemeStarryNightCheck, "starry-night"),
            };
            _presetOptions = new[]
            {
                new DropdownOption(PresetPlanOption, PresetPlanText, PresetPlanCheck, "plan"),
                new DropdownOption(PresetStandardOption, PresetStandardText, PresetStandardCheck, "standard"),
                new DropdownOption(PresetFullAccessOption, PresetFullAccessText, PresetFullAccessCheck, "full_access"),
            };
            if (ProviderListView is not null)
            {
                ProviderListView.ItemsSource = _providerRows;
            }
            // Row hover is owned by the item template's own visuals, so every row --
            // the fixed custom endpoint and the whole catalog -- reacts alike.
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
            if (TranscriptSizeOptionsPanel == null)
            {
                return;
            }

            var uiFont = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["RuUiFontFamily"];
            var muted = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuMutedBrush"];
            var ink = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuInkBrush"];
            var itemStyle = (Style)Application.Current.Resources["RuDropdownItemButtonStyle"];
            TranscriptSizeOptionsPanel.Children.Clear();
            var sizes = TranscriptSizePickerProjection.Sizes();
            for (int index = 0; index < sizes.Count; index++)
            {
                var size = sizes[index];
                // A live specimen: the "Aa" is set in the size itself, so the
                // choice is seen rather than read. The caption names the
                // activity size that comes with it.
                // Bottom-aligned: the captions share one baseline while the
                // larger specimens rise above, like a type specimen sheet.
                var specimen = new StackPanel
                {
                    Spacing = 0,
                    MinWidth = 72,
                    VerticalAlignment = VerticalAlignment.Bottom,
                };
                specimen.Children.Add(new TextBlock
                {
                    Text = "Aa",
                    FontFamily = uiFont,
                    FontSize = size,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ink,
                });
                specimen.Children.Add(new TextBlock
                {
                    Text = TranscriptSizePickerProjection.LabelWithActivity(size),
                    FontFamily = uiFont,
                    FontSize = 11,
                    Foreground = muted,
                });
                var option = new Button
                {
                    Content = specimen,
                    Tag = size,
                    Style = itemStyle,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                AutomationProperties.SetAutomationId(
                    option,
                    TranscriptSizePickerProjection.OptionAutomationId(size));
                option.Click += OnTranscriptSizeOptionClick;
                Grid.SetColumn(option, index);
                TranscriptSizeOptionsPanel.Children.Add(option);
            }

            RefreshTranscriptSizeSpecimens();
        }

        private void OnTranscriptSizeOptionClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: double size })
            {
                return;
            }

            TranscriptTypography.Instance.MessageFontSize = size;
            _transcriptSize = TranscriptTypography.Instance.MessageFontSize;
            RefreshTranscriptSizeSpecimens();
        }

        /// <summary>
        /// Marks the chosen specimen: an accent wash behind it and the "Aa"
        /// in accent, the rest at rest. Same selected language the dropdown
        /// options speak, so the tab reads as one system.
        /// </summary>
        private void RefreshTranscriptSizeSpecimens()
        {
            if (TranscriptSizeOptionsPanel == null) return;
            var selectedBrush = (Brush)Application.Current.Resources["RuAccentSoftBrush"];
            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var accent = (Brush)Application.Current.Resources["RuAccentBrush"];
            var ink = (Brush)Application.Current.Resources["RuInkBrush"];
            foreach (var child in TranscriptSizeOptionsPanel.Children)
            {
                if (child is not Button { Tag: double size } option) continue;
                bool selected = Math.Abs(size - _transcriptSize) < 0.001;
                option.Background = selected ? selectedBrush : transparent;
                if (option.Content is StackPanel specimen
                    && specimen.Children.Count > 0
                    && specimen.Children[0] is TextBlock glyph)
                {
                    glyph.Foreground = selected ? accent : ink;
                }
            }
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
            _modelsDevKeyTimer?.Stop();
            ProviderTestButton.IsEnabled = false;
            ShowProviderTestResult(ProviderConnectionTestPresentation.Testing());

            try
            {
                await SaveSettingsAsync();

                // A forced refresh is what actually re-queries the provider.
                await _providerCatalogController.RefreshAsync(force: true);
                var state = _providerCatalogController.State;
                var outcome = ProviderConnectionTestPresentation.Project(state);
                if (outcome.Tone == ProviderConnectionTest.ErrorTone)
                {
                    // The message alone cannot tell a blocked network apart from
                    // a bad key next time: record the catalog code locally.
                    GuiDiagnosticWriter.WriteGuiError("provider_test", state?.ErrorCode ?? "unknown");
                }

                ShowProviderTestResult(outcome, state?.ErrorCode);
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

        private void ShowProviderTestResult(ProviderConnectionTest result, string? errorCode = null)
        {
            if (ProviderTestStatusText is null)
            {
                return;
            }

            string code = errorCode?.Trim() ?? string.Empty;
            ProviderTestStatusText.Text = result.Message;
            var brush = (Brush)Application.Current.Resources[
                result.Tone switch
                {
                    ProviderConnectionTest.SuccessTone => "AppSuccessBrush",
                    ProviderConnectionTest.ErrorTone => "AppDangerBrush",
                    _ => "RuMutedBrush",
                }];
            ProviderTestStatusText.Foreground = brush;

            if (ProviderTestStatusIcon is not null)
            {
                ProviderTestStatusIcon.Glyph = result.Tone switch
                {
                    ProviderConnectionTest.SuccessTone => "\uE73E",
                    ProviderConnectionTest.ErrorTone => "\uE7BA",
                    _ => "\uE946",
                };
                ProviderTestStatusIcon.Foreground = brush;
            }

            // The raw catalog code stays available for a report without sitting in
            // the reading path; the diagnostic log keeps its own copy.
            ToolTipService.SetToolTip(
                ProviderTestStatusText,
                code.Length > 0 && code.Length <= 64 ? code : null);
        }

        /// <summary>
        /// Reflects a background catalog failure in the status line, but never a
        /// background success: the catalog also refreshes on a timer, and a result
        /// appearing without the button being pressed would read as the user's own.
        /// </summary>
        private void OnProviderCatalogStateChanged(object? sender, ProviderCatalogState state)
        {
            var outcome = ProviderConnectionTestPresentation.Project(state);
            if (!ProviderConnectionTestPresentation.ReportsFailure(outcome))
            {
                return;
            }

            // The catalog also refreshes on a timer, so this fires off the UI thread;
            // touching the status controls directly would be a cross-thread write.
            if (!DispatcherQueue.TryEnqueue(() => ShowProviderTestResult(outcome)))
            {
                GuiDiagnosticWriter.AppendTraceLine(
                    "[Settings] dispatcher rejected a provider test status update during shutdown.");
            }
        }

        public SettingsView()
        {
            this.InitializeComponent();
            InitDropdownOptions();
            InitTranscriptSizeOptions();
            InitializeSettingsHoverEffects();
            if (ProviderButton?.Flyout is Flyout providerFlyout)
            {
                providerFlyout.Opening += OnProviderFlyoutOpening;
            }
        }

        /// <summary>
        /// Gives the provider dropdown the whole height the window can spare.
        /// </summary>
        /// <remarks>
        /// The catalog is ~190 rows, so the picker is meant to be scrolled and searched;
        /// what it must never do is stop short. A cap of my own is what made the list read
        /// as truncated: at 560px it showed about eighteen rows and the rest were one
        /// slightly-off scrollbar away. The declared height in the markup is only the
        /// floor used when the window cannot be measured.
        /// </remarks>
        private void OnProviderFlyoutOpening(object? sender, object e)
        {
            if (ProviderButton is null || ProviderListView is null)
            {
                return;
            }

            double available = double.PositiveInfinity;
            if (ProviderButton.XamlRoot?.Size is { Height: > 0 } root)
            {
                try
                {
                    var origin = ProviderButton.TransformToVisual(null)
                        .TransformPoint(new Windows.Foundation.Point(0, ProviderButton.ActualHeight));
                    available = root.Height - origin.Y;
                }
                catch (Exception ex)
                {
                    // The trigger can be detached mid-layout; keep the declared height.
                    GuiDiagnosticWriter.AppendTraceLine($"[Settings] Provider flyout sizing skipped: {ex.Message}");
                }
            }

            // No upper cap of our own: the window is the only real limit, so a tall
            // window shows more rows and the list is never cut short by a constant.
            //
            // Height, not just MaxHeight: the picker's content lives in a StackPanel,
            // which measures its children with unbounded height. A ListView measured that
            // way realizes every item and lays none of them out. A concrete height is what
            // makes it virtualize and paint.
            double height = double.IsFinite(available)
                ? Math.Min(
                    Math.Max(
                        available - ProviderFlyoutBottomMargin,
                        ProviderFlyoutAbsoluteMinHeight),
                    ProviderFlyoutMaxHeight)
                : ProviderFlyoutMaxHeight;
            ProviderListView.MaxHeight = height;
            ProviderListView.Height = height;
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

            // The key box probes itself: a quiet box is saved and tested
            // without any button, one second after the last keystroke.
            _modelsDevKeyTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _modelsDevKeyTimer.Tick += OnModelsDevKeyTimerTick;

            CustomEndpointUrlBox.TextChanged += AutoSaveTextHandler;
            CustomApiKeyBox.PasswordChanged += AutoSaveHandler;
            CloudflareAccountIdBox.TextChanged += AutoSaveTextHandler;
            ModelsDevApiKeyBox.PasswordChanged += OnModelsDevKeyPasswordChanged;
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
                SetCurrentProvider(settings.BackendChoice ?? string.Empty);

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
                ApplyProviderKeys();
            }
            catch (Exception ex)
            {
                _modelsDevLoadFailed = true;
                GuiDiagnosticWriter.AppendTraceLine($"[Settings] Error loading Models.dev providers: {ex.Message}");
                // The failure mode this replaced was silent: a partially built list with
                // no error anywhere. The stage and the row count make it visible next time.
                GuiDiagnosticWriter.AppendTraceLine(
                    $"[provider-list] load-failed: type={ex.GetType().Name} "
                    + $"hresult=0x{ex.HResult:X8} rows={_providerRows.Count}");
                ApplyProviderFilter(ProviderSearchBox?.Text);
            }
        }

        /// <summary>
        /// Lights the key dot on every row whose provider has a stored key.
        /// </summary>
        private void ApplyProviderKeys()
        {
            var keys = new HashSet<string>(_modelsDevKeyIds, StringComparer.OrdinalIgnoreCase);
            foreach (var row in _providerRows)
            {
                row.HasKey = row.Value == "custom"
                    ? _lastSettings?.HasCustomKey == true
                    : keys.Contains(row.Value[ProviderSetupGuidance.ModelsDevPrefix.Length..]);
            }

            MoveSignedInRowsToTop();
        }

        /// <summary>
        /// Signed-in providers lead the picker: a stored key is the strongest signal
        /// of intent, and the ~190-row catalog is otherwise a long scroll. The order
        /// inside each group stays alphabetical.
        /// </summary>
        private void MoveSignedInRowsToTop()
        {
            var ordered = _providerRows
                .OrderByDescending(row => row.HasKey)
                .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int index = 0; index < ordered.Count; index++)
            {
                int current = _providerRows.IndexOf(ordered[index]);
                if (current != index)
                {
                    _providerRows.Move(current, index);
                }
            }
        }

        /// <summary>
        /// Replaces the picker's rows with the custom endpoint plus the whole
        /// Models.dev catalog, in A-Z order.
        /// </summary>
        /// <remarks>
        /// One collection drives the ListView, so the catalog's size costs nothing at
        /// build time: the list materializes rows as they scroll into view. The custom
        /// endpoint stays first-class because it is the only provider the catalog cannot
        /// describe -- a server on this machine, or a gateway with its own base URL.
        /// </remarks>
        private void RebuildModelsDevOptions(IReadOnlyList<ModelsDevEntryDto> providers)
        {
            _modelsDevNames.Clear();
            foreach (var (id, name) in ProviderSetupGuidance.OrderModelsDevOptions(
                         providers.Select(entry => (entry.Id, entry.Name))))
            {
                _modelsDevNames[id] = name;
            }

            var rows = new List<ProviderRow>(_modelsDevNames.Count + 1)
            {
                new("custom", CustomProviderDisplayName),
            };
            foreach (var id in _modelsDevNames.Keys)
            {
                rows.Add(new ProviderRow(ProviderSetupGuidance.ModelsDevPrefix + id, _modelsDevNames[id]));
            }

            rows.Sort(static (left, right) =>
            {
                int byName = string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
                return byName != 0
                    ? byName
                    : string.Compare(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);
            });

            _providerRows.Clear();
            foreach (var row in rows)
            {
                _providerRows.Add(row);
            }

            _providerChoice = ProviderSetupGuidance.ResolveUnsetChoice(
                _providerChoice,
                providers.Select(entry => (entry.Id, entry.Name)));
            ApplyProviderFilter(ProviderSearchBox?.Text);
            LogProviderListState("rebuild");
        }

        /// <summary>
        /// The custom endpoint's label. Declared once so the picker and the key row
        /// cannot drift apart.
        /// </summary>
        private const string CustomProviderDisplayName = "Custom OpenAI Server";

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
            int matching = 0;
            foreach (var row in _providerRows)
            {
                var id = ProviderSetupGuidance.IsModelsDevChoice(row.Value)
                    ? row.Value[ProviderSetupGuidance.ModelsDevPrefix.Length..]
                    : row.Value;
                bool show = ProviderSetupGuidance.MatchesProviderFilter(row.DisplayName, id, filter);
                row.IsVisible = show;
                if (show)
                {
                    matching++;
                }
            }

            if (FixedProvidersHeader is not null)
            {
                int total = _providerRows.Count;
                // The count stays visible even at zero matches. Hiding the header exactly
                // when a search finds nothing left the panel with no guidance at all, and
                // "PROVIDERS (0 of 191)" is the honest answer: the catalog is there, the
                // query is what is narrow.
                FixedProvidersHeader.Text = _modelsDevLoadFailed && total <= 1
                    ? $"PROVIDERS ({total}) - Models.dev list unavailable"
                    : matching < total
                        ? $"PROVIDERS ({matching} of {total})"
                        : $"PROVIDERS ({total})";
            }

            if (ProviderNoMatchText is not null)
            {
                ProviderNoMatchText.Visibility = matching == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            RefreshProviderUI();
            LogProviderListState("filter");
        }

        /// <summary>
        /// Records how many provider rows the picker holds and how many it shows.
        /// </summary>
        /// <remarks>
        /// The catalog is ~190 rows. A short read here is what identified the real fault
        /// behind the picker stopping in the C's: building one Button per provider threw
        /// 0x800F1000 partway through, so the list was truncated at the point of failure
        /// while every layer above it was returning the full catalog.
        /// </remarks>
        private void LogProviderListState(string stage)
        {
            try
            {
                int visible = _providerRows.Count(row => row.IsVisible);

                GuiDiagnosticWriter.AppendTraceLine(
                    $"[provider-list] {stage}: rows={_providerRows.Count} "
                    + $"visible={visible} "
                    + $"filter='{ProviderSearchBox?.Text}' "
                    + $"loadFailed={_modelsDevLoadFailed}");
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[provider-list] {stage}: logging failed: {ex.Message}");
            }
        }

        private void OnModelsDevKeyPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _modelsDevKeyTimer?.Stop();
            if (string.IsNullOrEmpty(ModelsDevApiKeyBox.Password))
            {
                ModelsDevKeyStatus.Text = string.Empty;
                return;
            }

            // Typing restarts the verdict below; only a quiet box gets probed.
            ModelsDevKeyStatus.Text = string.Empty;
            _modelsDevKeyTimer?.Start();
        }

        private async void OnModelsDevKeyTimerTick(object? sender, object e)
        {
            _modelsDevKeyTimer?.Stop();
            await ProbeAndSaveModelsDevKeyAsync();
        }

        /// <summary>
        /// Saves the typed key only after it proves itself: the key is stored,
        /// probed with a forced catalog refresh, and removed again when the
        /// provider answers "unauthorized". Transport failures keep the key and
        /// say so, because a dead network is not a wrong key.
        /// </summary>
        private async Task ProbeAndSaveModelsDevKeyAsync()
        {
            if (_loading || _modelsDevKeyProbeRunning) return;
            if (_apiService is null || _providerCatalogController is null) return;
            if (!ProviderSetupGuidance.IsModelsDevChoice(_providerChoice)) return;
            string apiKey = ModelsDevApiKeyBox.Password;
            if (string.IsNullOrWhiteSpace(apiKey)) return;

            // One shared table decides the provider's canonical form and key field, so
            // this path and the terminal's /key cannot disagree about what the backend
            // accepts. They did once: the bare id was sent and every catalog key was
            // rejected as an invalid payload.
            var target = ProviderCredential.Resolve(_providerChoice);
            if (target is null || !target.IsModelsDev) return;
            string providerId = target.Choice;
            string displayId = target.DisplayName;
            _modelsDevKeyProbeRunning = true;
            try
            {
                SetModelsDevKeyStatus("Checking key…", "info");
                bool saved = await _apiService.SaveModelsDevKeyAsync(providerId, apiKey);
                if (!saved)
                {
                    SetModelsDevKeyStatus("Save failed; try again.", ProviderConnectionTest.ErrorTone);
                    return;
                }

                await _providerCatalogController.RefreshAsync(force: true);
                var state = _providerCatalogController.State;
                var outcome = ProviderConnectionTestPresentation.Project(state);
                if (outcome.Tone == ProviderConnectionTest.SuccessTone)
                {
                    ModelsDevApiKeyBox.Password = string.Empty;
                    await LoadSettingsAsync();
                    // LoadSettingsAsync clears the status line, so the success
                    // message must land after the reload, not before it.
                    SetModelsDevKeyStatus($"Saved key for {displayId}.", ProviderConnectionTest.SuccessTone);
                }
                else if (string.Equals(state?.ErrorCode, "catalog_unauthorized", StringComparison.OrdinalIgnoreCase))
                {
                    await _apiService.RemoveProviderAsync(providerId);
                    SetModelsDevKeyStatus("Invalid key; not saved.", ProviderConnectionTest.ErrorTone);
                }
                else
                {
                    string code = state?.ErrorCode?.Trim() ?? "unknown";
                    SetModelsDevKeyStatus(
                        $"Could not verify the connection [{code}]; key kept.",
                        "info");
                }
            }
            finally
            {
                _modelsDevKeyProbeRunning = false;
            }
        }

        private void SetModelsDevKeyStatus(string message, string tone)
        {
            ModelsDevKeyStatus.Text = message;
            ModelsDevKeyStatus.Foreground = (Brush)Application.Current.Resources[
                tone switch
                {
                    ProviderConnectionTest.SuccessTone => "AppSuccessBrush",
                    ProviderConnectionTest.ErrorTone => "AppDangerBrush",
                    _ => "RuMutedBrush",
                }];
        }

        private void UpdateVisibilityForBackend(string choice)
        {
            bool isModelsDev = ProviderSetupGuidance.IsModelsDevChoice(choice);
            CustomEndpointRow.Visibility = (!isModelsDev && choice == "custom") ? Visibility.Visible : Visibility.Collapsed;
            CustomApiKeyRow.Visibility = (!isModelsDev && choice == "custom") ? Visibility.Visible : Visibility.Collapsed;
            if (ModelsDevApiKeyRow is not null)
            {
                ModelsDevApiKeyRow.Visibility = isModelsDev ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ModelsDevApiKeyLabel is not null)
            {
                // The button label already resolves the display name (runtime rows,
                // then the fetched models.dev list); reuse it so the key row names
                // the same provider the user picked.
                string name = ProviderButtonLabel?.Text?.Trim() ?? string.Empty;
                if (name.Length == 0 || string.Equals(name, "No provider", StringComparison.Ordinal))
                {
                    name = ProviderSetupGuidance.DisplayName(choice);
                }

                ModelsDevApiKeyLabel.Text = isModelsDev ? $"{name} API key" : "API key";
            }

            if (CloudflareAccountIdBox is not null)
            {
                CloudflareAccountIdBox.Visibility = IsCloudflareChoice(choice) ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ModelsDevSignOutButton is not null)
            {
                // Signing out only makes sense while a key is actually stored; the
                // dot in the picker and this button read the same source.
                ModelsDevSignOutButton.Visibility = isModelsDev && IsActiveProviderSignedIn(choice)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Whether the given provider has a stored credential: the custom key flag
        /// or one of the fetched models.dev key ids.
        /// </summary>
        private bool IsActiveProviderSignedIn(string choice)
        {
            if (string.Equals(choice, "custom", StringComparison.OrdinalIgnoreCase))
            {
                return _lastSettings?.HasCustomKey == true;
            }

            if (!ProviderSetupGuidance.IsModelsDevChoice(choice))
            {
                return false;
            }

            string id = choice[ProviderSetupGuidance.ModelsDevPrefix.Length..];
            return _modelsDevKeyIds.Contains(id, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Removes the stored key of the active provider. The key can be pasted
        /// again to sign back in, so no confirmation step is needed.
        /// </summary>
        private async void OnSignOutActiveProviderClick(object sender, RoutedEventArgs e)
        {
            if (_apiService is null || string.IsNullOrWhiteSpace(_providerChoice))
            {
                return;
            }

            if (sender is Button button)
            {
                button.IsEnabled = false;
            }

            string name = ProviderButtonLabel?.Text?.Trim() ?? _providerChoice;
            try
            {
                bool removed = await _apiService.RemoveProviderAsync(_providerChoice);
                if (removed)
                {
                    ClearProviderKeyInput(_providerChoice);
                    await LoadSettingsAsync();
                    // LoadSettingsAsync clears the status line, so the confirmation
                    // must land after the reload, not before it.
                    SetModelsDevKeyStatus($"Signed out of {name}. Paste a new key to sign in again.", ProviderConnectionTest.SuccessTone);
                }
                else
                {
                    SetModelsDevKeyStatus("Could not sign out; the backend did not answer.", ProviderConnectionTest.ErrorTone);
                }
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.WriteCrashEvent(ex);
                SetModelsDevKeyStatus("Could not sign out; try again.", ProviderConnectionTest.ErrorTone);
            }
            finally
            {
                if (sender is Button released)
                {
                    released.IsEnabled = true;
                }
            }
        }

        private bool IsCloudflareChoice(string choice) =>
            string.Equals(
                ProviderSetupGuidance.NormalizeProviderChoice(choice),
                "models.dev:cloudflare-workers-ai",
                StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Commits the clicked row as the active provider and closes the picker.
        /// </summary>
        private void OnProviderRowClicked(object sender, ItemClickEventArgs e)
        {
            if (_loading) return;
            if (e.ClickedItem is not ProviderRow row) return;
            if (!ProviderSetupGuidance.IsSelectableProvider(row.Value)) return;

            _providerChoice = ProviderSetupGuidance.NormalizeProviderChoice(row.Value);
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
                    CustomUrl = CustomEndpointUrlBox.Text?.Trim() ?? "http://localhost:8000/v1",
                    CustomApiKey = CustomApiKeyBox.Password,
                    CloudflareAccountId = CloudflareAccountIdBox.Text?.Trim() ?? string.Empty,
                };

                bool saved = await _apiService.SaveProviderSettingsAsync(dto);
                if (!saved)
                {
                    GuiDiagnosticWriter.WriteGuiError("settings_autosave", "save_failed");
                }
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.WriteCrashEvent(ex);
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
                bool removed = await _apiService.RemoveProviderAsync(alias);
                if (removed)
                {
                    ClearProviderKeyInput(alias);
                    await LoadSettingsAsync();
                }
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.WriteCrashEvent(ex);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        private void ClearProviderKeyInput(string alias)
        {
            if (ProviderSetupGuidance.IsModelsDevChoice(alias))
            {
                ModelsDevApiKeyBox.Password = string.Empty;
                return;
            }

            switch (alias)
            {
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
                        static faulted => GuiDiagnosticWriter.AppendTraceLine(
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
            CustomEndpointUrlBox.TextChanged -= AutoSaveTextHandler;
            CustomApiKeyBox.PasswordChanged -= AutoSaveHandler;
            CloudflareAccountIdBox.TextChanged -= AutoSaveTextHandler;
            if (ProviderButton?.Flyout is Flyout providerFlyout)
            {
                providerFlyout.Opening -= OnProviderFlyoutOpening;
            }
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
            ModelsDevApiKeyBox.PasswordChanged -= OnModelsDevKeyPasswordChanged;
            if (_modelsDevKeyTimer != null)
            {
                _modelsDevKeyTimer.Tick -= OnModelsDevKeyTimerTick;
                _modelsDevKeyTimer.Stop();
                _modelsDevKeyTimer = null;
            }
        }

        private void AutoSaveHandler(object sender, RoutedEventArgs e) => ScheduleAutoSave();

        private void AutoSaveTextHandler(object sender, TextChangedEventArgs e) => ScheduleAutoSave();

        private void OnThemeOptionClick(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (sender is not Button button || button.Tag is not string tag) return;
            _themeChoice = tag.ToLowerInvariant() switch
            {
                "dark" => "dark",
                "dark-meteor" => "dark-meteor",
                "starry-night" => "starry-night",
                _ => "light",
            };
            RefreshThemeUI();
            (ThemeButton.Flyout as Flyout)?.Hide();
            var theme = _themeChoice switch
            {
                "dark" => UiTheme.Dark,
                "dark-meteor" => UiTheme.DarkMeteorShower,
                "starry-night" => UiTheme.StarryNight,
                _ => UiTheme.Light,
            };
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
