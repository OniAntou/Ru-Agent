using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuApp.Models;
using RuApp.Services;
using RuApp.ViewModels;

using RuAgent.Protocol.Harness;
namespace RuApp.Views
{
    /// <summary>
    /// Renders <see cref="SettingsViewModel"/>. Decisions about providers, keys, autosave
    /// and the connection test live in the view model; this class owns only what needs
    /// WinUI: brushes, flyouts, layout measurement and the transcript-size specimens.
    /// </summary>
    public sealed partial class SettingsView : UserControl, IDisposable
    {
        private ProviderCatalogController? _providerCatalogController;
        private bool _ownsProviderCatalogController;

        public SettingsViewModel ViewModel { get; }

        /// <summary>The remembered approval rules page.</summary>
        public ViewModels.ApprovalRulesViewModel Rules { get; }

        private Func<Services.IApprovalRulesClient?> _rulesClient = () => null;
        private Func<IReadOnlyList<ViewModels.ApprovalRuleWorkspace>> _rulesWorkspaces = () => [];

        /// <summary>Where the rules page reads from: the run controller's client and the sidebar's workspaces.</summary>
        public void ConfigureApprovalRules(
            Func<Services.IApprovalRulesClient?> client,
            Func<IReadOnlyList<ViewModels.ApprovalRuleWorkspace>> workspaces)
        {
            _rulesClient = client ?? throw new ArgumentNullException(nameof(client));
            _rulesWorkspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        }

        public event Action<UiTheme>? ThemeChanged
        {
            add => ViewModel.ThemeChanged += value;
            remove => ViewModel.ThemeChanged -= value;
        }

        public event Action<AgentPermissionPreset>? PresetChanged
        {
            add => ViewModel.PresetChanged += value;
            remove => ViewModel.PresetChanged -= value;
        }

        /// <summary>
        /// The stored conversation text size, read once at construction so the picker
        /// opens on the size the transcript is already using.
        /// </summary>
        private double _transcriptSize = TranscriptTypography.Instance.MessageFontSize;

        private sealed record DropdownOption(Button Button, TextBlock Label, FontIcon Check, string Value);

        private IReadOnlyList<DropdownOption>? _themeOptions;
        private IReadOnlyList<DropdownOption>? _presetOptions;

        public SettingsView()
        {
            ViewModel = new SettingsViewModel(DispatchToUi);
            Rules = new ViewModels.ApprovalRulesViewModel(() => _rulesClient(), () => _rulesWorkspaces());
            this.InitializeComponent();
            InitializeSettingsLayout();
            InitializeUpdates();
            InitDropdownOptions();
            InitTranscriptSizeOptions();
            InitializeSettingsHoverEffects();
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ViewModel.ProviderPickerCommitted += OnProviderPickerCommitted;
            if (ProviderButton?.Flyout is Flyout providerFlyout)
            {
                providerFlyout.Opening += OnProviderFlyoutOpening;
            }
        }

        public void SetCurrentTheme(UiTheme theme) => ViewModel.SetCurrentTheme(theme);

        public void SetCurrentPreset(AgentPermissionPreset preset) => ViewModel.SetCurrentPreset(preset);

        public void SetCurrentProvider(string backendChoice) => ViewModel.SetCurrentProvider(backendChoice);

        public void Initialize(ApiService apiService, ProviderCatalogController? providerCatalogController = null)
        {
            // A previous Initialize that owned its controller must not leave it behind
            // when the view is re-initialized against a new backend.
            DisposeOwnedProviderCatalogController();
            _providerCatalogController = providerCatalogController ?? new ProviderCatalogController(new ProviderCatalogService(apiService));
            _ownsProviderCatalogController = providerCatalogController is null;
            _ = ViewModel.AttachAsync(new ApiSettingsBackend(apiService), new ProviderCatalogSession(_providerCatalogController));
        }

        public Task RefreshProviderCatalogAsync() => ViewModel.RefreshCatalogAsync();

        private void DispatchToUi(Action action)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                action();
            }
            else if (!DispatcherQueue.TryEnqueue(() => action()))
            {
                GuiDiagnosticWriter.AppendTraceLine("[Settings] dispatcher rejected a view model update during shutdown.");
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(SettingsViewModel.ThemeChoice):
                    RefreshThemeUI();
                    break;
                case nameof(SettingsViewModel.PresetChoice):
                    RefreshPresetUI();
                    break;
                case nameof(SettingsViewModel.IsProviderEditorOpen):
                    if (ViewModel.IsProviderEditorOpen) FocusProviderEditor();
                    else DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!ViewModel.IsProviderEditorOpen && IsLoaded) ProviderButton.Focus(FocusState.Programmatic);
                    });
                    break;
            }
        }

        /// <summary>Maps a status tone to its theme brush; used by x:Bind.</summary>
        public Brush ToneBrush(string tone) => (Brush)Application.Current.Resources[tone switch
        {
            ProviderConnectionTest.SuccessTone => "AppSuccessBrush",
            ProviderConnectionTest.ErrorTone => "AppDangerBrush",
            _ => "RuMutedBrush",
        }];

        /// <summary>Maps a status tone to its glyph; used by x:Bind.</summary>
        public string ToneGlyph(string tone) => tone switch
        {
            ProviderConnectionTest.SuccessTone => "",
            ProviderConnectionTest.ErrorTone => "",
            _ => "",
        };

        private void RefreshThemeUI()
        {
            if (_themeOptions == null || ThemeButtonLabel == null) return;
            RefreshDropdownUI(_themeOptions, ViewModel.ThemeChoice, ThemeButtonLabel);
        }

        private void RefreshPresetUI()
        {
            if (_presetOptions == null || PresetButtonLabel == null) return;
            RefreshDropdownUI(_presetOptions, ViewModel.PresetChoice, PresetButtonLabel);
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
            };
            _presetOptions = new[]
            {
                new DropdownOption(PresetPlanOption, PresetPlanText, PresetPlanCheck, "plan"),
                new DropdownOption(PresetStandardOption, PresetStandardText, PresetStandardCheck, "standard"),
                new DropdownOption(PresetFullAccessOption, PresetFullAccessText, PresetFullAccessCheck, "full_access"),
            };
            RefreshThemeUI();
            RefreshPresetUI();
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

            var uiFont = (FontFamily)Application.Current.Resources["RuUiFontFamily"];
            var muted = (Brush)Application.Current.Resources["RuMutedBrush"];
            var ink = (Brush)Application.Current.Resources["RuInkBrush"];
            var itemStyle = (Style)Application.Current.Resources["RuDropdownItemButtonStyle"];
            TranscriptSizeOptionsPanel.Children.Clear();
            var sizes = TranscriptSizePickerProjection.Sizes();
            for (int index = 0; index < sizes.Count; index++)
            {
                var size = sizes[index];
                // A live specimen: the "Aa" is set in the size itself, so the choice is
                // seen rather than read. Bottom-aligned so the captions share a baseline.
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
                AutomationProperties.SetAutomationId(option, TranscriptSizePickerProjection.OptionAutomationId(size));
                AutomationProperties.SetName(option, $"Conversation text size {TranscriptSizePickerProjection.LabelWithActivity(size)}");
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
        /// Marks the chosen specimen with the same selected language the dropdowns use.
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
                AutomationProperties.SetItemStatus(option, selected ? "Selected" : "Not selected");
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
        /// Gives the provider dropdown the whole height the window can spare.
        /// </summary>
        /// <remarks>
        /// The catalog is ~190 rows, so the picker is meant to be scrolled and searched;
        /// what it must never do is stop short. The declared height in the markup is only
        /// the floor used when the window cannot be measured.
        /// </remarks>
        private void OnProviderFlyoutOpening(object? sender, object e)
        {
            if (ProviderButton is null || ProviderListView is null)
            {
                return;
            }
            ViewModel.ProviderFilter = string.Empty;

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

            // Height, not just MaxHeight: the picker lives in a StackPanel, whose
            // unbounded measure would otherwise realize every row and paint none.
            double height = SettingsProviderPresenter.LayoutFor(available).Height;
            ProviderListView.MaxHeight = height;
            ProviderListView.Height = height;
        }

        private void OnProviderRowClicked(object sender, ItemClickEventArgs e) =>
            ViewModel.SelectProviderCommand.Execute(e.ClickedItem as ProviderRow);

        private void OnProviderPickerCommitted()
        {
            (ProviderButton.Flyout as Flyout)?.Hide();
            FocusProviderEditor();
        }

        private void OnThemeOptionClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tag }) return;
            ViewModel.SelectThemeCommand.Execute(tag);
            (ThemeButton.Flyout as Flyout)?.Hide();
        }

        private void OnPresetOptionClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tag }) return;
            ViewModel.SelectPresetCommand.Execute(tag);
            (PresetButton.Flyout as Flyout)?.Hide();
        }

        public void Dispose()
        {
            _updateLifetime.Cancel();
            StopUpdateAvailabilityChecks();
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.ProviderPickerCommitted -= OnProviderPickerCommitted;
            if (ProviderButton?.Flyout is Flyout providerFlyout)
            {
                providerFlyout.Opening -= OnProviderFlyoutOpening;
            }

            ViewModel.Dispose();
            DisposeOwnedProviderCatalogController();
        }

        /// <summary>
        /// Disposes the provider catalog controller when this view owns it. Async disposal
        /// never blocks the UI thread; a fault is only logged.
        /// </summary>
        private void DisposeOwnedProviderCatalogController()
        {
            if (_providerCatalogController is null)
            {
                return;
            }

            if (_ownsProviderCatalogController)
            {
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
    }
}
