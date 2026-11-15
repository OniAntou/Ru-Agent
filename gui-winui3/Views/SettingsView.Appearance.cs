using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using RuApp.Services;

namespace RuApp.Views
{
    /// <summary>
    /// Settings &gt; Appearance. Every control writes through <see cref="AppearanceState"/>,
    /// which saves the file and tells the shell to repaint; this view only mirrors the
    /// current preferences back into its controls.
    /// </summary>
    public sealed partial class SettingsView
    {
        private sealed record ColorSlot(
            string Label,
            Func<AppearanceColorSet, string?> Get,
            Func<AppearanceColorSet, string?, AppearanceColorSet> Set,
            Func<RuThemePalettes.ThemePalette, Windows.UI.Color> Effective);

        private static readonly ColorSlot[] ColorSlots =
        [
            new("Canvas", s => s.Canvas, (s, v) => s with { Canvas = v }, p => p.Canvas),
            new("Sidebar", s => s.Sidebar, (s, v) => s with { Sidebar = v }, p => p.Sidebar),
            new("Cards and panels", s => s.Surface, (s, v) => s with { Surface = v }, p => p.Surface),
            new("Text", s => s.Text, (s, v) => s with { Text = v }, p => p.Ink),
            new("Secondary text", s => s.MutedText, (s, v) => s with { MutedText = v }, p => p.Muted),
            new("Accent", s => s.Accent, (s, v) => s with { Accent = v }, p => p.Accent),
            new("Borders", s => s.Border, (s, v) => s with { Border = v }, p => p.Line),
        ];

        /// <summary>Suggestions only: the boxes accept any installed family name.</summary>
        private static readonly string[] UiFontSuggestions =
            ["Default", "Inter", "Segoe UI Variable", "Segoe UI", "Arial", "Calibri", "Georgia", "Verdana", "Bahnschrift"];

        private static readonly string[] CodeFontSuggestions =
            ["Default", "Cascadia Code", "Cascadia Mono", "Consolas", "Courier New", "Lucida Console"];

        private const string DefaultFontChoice = "Default";

        private static AppearanceState Appearance => AppearanceState.Instance;

        private readonly List<(ColorSlot Slot, Border Swatch, TextBlock Value)> _colorRows = [];
        private UiTheme _appearanceTheme = MainWindow.CurrentTheme;
        private bool _appearanceSyncing;

        /// <summary>
        /// True while the controls are being built or filled from the preferences. The
        /// slider coerces its value to its minimum during InitializeComponent and the
        /// combo boxes fire on programmatic selection; neither is a user edit.
        /// </summary>
        private bool AppearanceInputIgnored => !_appearanceInitialized || _appearanceSyncing;
        private bool _appearanceInitialized;

        private void InitializeAppearanceSettings()
        {
            if (_appearanceInitialized) return;
            _appearanceInitialized = true;
            foreach (var name in UiFontSuggestions) AppearanceUiFontBox.Items.Add(name);
            foreach (var name in CodeFontSuggestions) AppearanceCodeFontBox.Items.Add(name);
            BuildColorRows();
            Appearance.Changed += OnAppearanceStateChanged;
            Unloaded += (_, _) => Appearance.Changed -= OnAppearanceStateChanged;
            Loaded += (_, _) =>
            {
                Appearance.Changed -= OnAppearanceStateChanged;
                Appearance.Changed += OnAppearanceStateChanged;
                RefreshAppearanceControls();
            };
            RefreshAppearanceControls();
        }

        private void OnAppearanceStateChanged(object? sender, EventArgs e)
        {
            if (DispatcherQueue.HasThreadAccess) OnAppearanceApplied();
            else DispatcherQueue.TryEnqueue(OnAppearanceApplied);
        }

        private void OnAppearanceApplied()
        {
            RefreshAppearanceControls();
            // The rail paints its active tab with brushes looked up in code, which keep
            // the colors they resolved to; re-highlighting picks up the new palette.
            if (_shownTab is { } tab && !_searchHidPanels)
            {
                _shownTab = null;
                SelectTab(tab);
            }
        }

        /// <summary>The color rows show and edit the overrides for the theme now on screen.</summary>
        private void SetAppearanceTheme(UiTheme theme)
        {
            _appearanceTheme = theme;
            if (_appearanceInitialized) RefreshAppearanceControls();
        }

        private void RefreshAppearanceControls()
        {
            if (!_appearanceInitialized) return;
            var current = Appearance.Current;
            _appearanceSyncing = true;
            try
            {
                AppearanceBackgroundStatus.Text = current.BackgroundKind switch
                {
                    AppearanceBackgroundKind.Image when Appearance.BackgroundPath is not null => "Showing your picture behind the window",
                    AppearanceBackgroundKind.Video when Appearance.BackgroundPath is not null => "Playing your video behind the window, muted and looping",
                    _ => "A picture or a muted, looping video behind the whole window",
                };
                AppearanceRemoveBackgroundButton.IsEnabled = current.HasBackground;
                SelectByTag(AppearanceFitBox, current.BackgroundFit.ToString());
                AppearanceOpacitySlider.Value = Math.Round(current.PanelOpacity * 100);
                SelectByTag(AppearanceCornersBox, current.CornerStyle.ToString());
                SelectByTag(AppearanceSidebarBox, current.SidebarSide.ToString());
                AppearanceUiFontBox.Text = current.UiFont ?? string.Empty;
                AppearanceCodeFontBox.Text = current.CodeFont ?? string.Empty;
                AppearanceRestartNote.Visibility = Appearance.RestartPending ? Visibility.Visible : Visibility.Collapsed;
                RefreshColorRows(current);
            }
            finally
            {
                _appearanceSyncing = false;
            }
        }

        private void BuildColorRows()
        {
            for (int index = 0; index < ColorSlots.Length; index++)
            {
                var slot = ColorSlots[index];
                if (index % 2 == 0) AppearanceColorRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var swatch = new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = UiCornerRadius.MicroPill,
                    BorderThickness = new Thickness(1),
                    BorderBrush = (Brush)Application.Current.Resources["RuLineBrush"],
                };
                var value = new TextBlock
                {
                    Style = (Style)Resources["SettingDescStyle"],
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                content.Children.Add(swatch);
                content.Children.Add(value);
                var button = new Button
                {
                    Content = content,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    MinWidth = 120,
                    Flyout = BuildColorFlyout(slot),
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{slot.Label} color");

                var row = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
                var label = new TextBlock
                {
                    Text = slot.Label,
                    Style = (Style)Resources["SettingLabelStyle"],
                    VerticalAlignment = VerticalAlignment.Center,
                };
                row.Children.Add(label);
                Grid.SetColumn(button, 1);
                row.Children.Add(button);
                Grid.SetRow(row, index / 2);
                Grid.SetColumn(row, index % 2);
                AppearanceColorRows.Children.Add(row);
                _colorRows.Add((slot, swatch, value));
            }
        }

        private Flyout BuildColorFlyout(ColorSlot slot)
        {
            var picker = new ColorPicker
            {
                IsAlphaEnabled = false,
                IsMoreButtonVisible = false,
                IsColorChannelTextInputVisible = false,
                IsHexInputVisible = true,
            };
            var flyout = new Flyout();
            var apply = new Button { Content = "Apply" };
            var useTheme = new Button { Content = "Use theme color" };
            apply.Click += (_, _) =>
            {
                flyout.Hide();
                SetSlotColor(slot, AppearanceColor.ToHex(picker.Color));
            };
            useTheme.Click += (_, _) =>
            {
                flyout.Hide();
                SetSlotColor(slot, null);
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(useTheme);
            buttons.Children.Add(apply);
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(picker);
            panel.Children.Add(buttons);
            flyout.Content = panel;
            flyout.Opening += (_, _) => picker.Color = slot.Effective(EffectivePalette());
            return flyout;
        }

        private RuThemePalettes.ThemePalette EffectivePalette()
        {
            bool isDark = RuThemePalettes.IsDark(_appearanceTheme);
            return AppearancePalette.Build(
                RuThemePalettes.PaletteFor(_appearanceTheme),
                Appearance.Current.ColorsFor(isDark),
                isDark);
        }

        private void RefreshColorRows(AppearancePreferences current)
        {
            bool isDark = RuThemePalettes.IsDark(_appearanceTheme);
            AppearanceColorsTitle.Text = isDark ? "Colors for dark themes" : "Colors for the light theme";
            var overrides = current.ColorsFor(isDark);
            var palette = EffectivePalette();
            foreach (var (slot, swatch, value) in _colorRows)
            {
                var color = slot.Effective(palette);
                swatch.Background = new SolidColorBrush(color);
                bool custom = slot.Get(overrides) is not null;
                value.Text = custom ? AppearanceColor.ToHex(color) : "Theme";
            }
        }

        private void SetSlotColor(ColorSlot slot, string? hex)
        {
            bool isDark = RuThemePalettes.IsDark(_appearanceTheme);
            SaveAppearance(current => current.WithColors(isDark, slot.Set(current.ColorsFor(isDark), hex)));
        }

        private void OnAppearanceResetColorsClick(object sender, RoutedEventArgs e)
        {
            bool isDark = RuThemePalettes.IsDark(_appearanceTheme);
            SaveAppearance(current => current.WithColors(isDark, AppearanceColorSet.Empty));
        }

        private async void OnAppearanceChooseBackgroundClick(object sender, RoutedEventArgs e)
        {
            HideAppearanceError();
            if (XamlRoot?.ContentIslandEnvironment?.AppWindowId is not { } windowId) return;
            string? path;
            try
            {
                var picker = new FileOpenPicker(windowId) { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
                foreach (var extension in AppearanceMediaFiles.PickerExtensions) picker.FileTypeFilter.Add(extension);
                path = (await picker.PickSingleFileAsync())?.Path;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                path = null;
            }

            if (string.IsNullOrEmpty(path)) return;
            AppearanceChooseBackgroundButton.IsEnabled = false;
            try
            {
                var imported = await Appearance.Media.ImportAsync(path);
                if (!imported.Succeeded)
                {
                    ShowAppearanceError(imported.Error!);
                    return;
                }

                SaveAppearance(current => current with
                {
                    BackgroundKind = imported.Kind,
                    BackgroundFile = imported.FileName,
                });
            }
            finally
            {
                AppearanceChooseBackgroundButton.IsEnabled = true;
            }
        }

        private void OnAppearanceRemoveBackgroundClick(object sender, RoutedEventArgs e)
        {
            HideAppearanceError();
            SaveAppearance(current => current with
            {
                BackgroundKind = AppearanceBackgroundKind.None,
                BackgroundFile = null,
            });
        }

        private void OnAppearanceFitChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AppearanceInputIgnored || SelectedTag(AppearanceFitBox) is not { } tag
                || !Enum.TryParse<AppearanceBackgroundFit>(tag, out var fit)) return;
            SaveAppearance(current => current with { BackgroundFit = fit });
        }

        private void OnAppearanceOpacityChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (AppearanceInputIgnored) return;
            SaveAppearance(current => current with { PanelOpacity = e.NewValue / 100 });
        }

        private void OnAppearanceCornersChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AppearanceInputIgnored || SelectedTag(AppearanceCornersBox) is not { } tag
                || !Enum.TryParse<AppearanceCornerStyle>(tag, out var style)) return;
            SaveAppearance(current => current with { CornerStyle = style });
        }

        private void OnAppearanceSidebarChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AppearanceInputIgnored || SelectedTag(AppearanceSidebarBox) is not { } tag
                || !Enum.TryParse<AppearanceSidebarSide>(tag, out var side)) return;
            SaveAppearance(current => current with { SidebarSide = side });
        }

        private void OnAppearanceFontSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AppearanceInputIgnored || sender is not ComboBox { SelectedItem: string name } box) return;
            SaveFont(box, name);
        }

        private void OnAppearanceFontSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
        {
            SaveFont(sender, args.Text);
            args.Handled = true;
        }

        private void SaveFont(ComboBox box, string? name)
        {
            var font = string.Equals(name?.Trim(), DefaultFontChoice, StringComparison.OrdinalIgnoreCase)
                ? null
                : AppearancePreferences.NormalizeFontName(name);
            if (ReferenceEquals(box, AppearanceUiFontBox)) SaveAppearance(current => current with { UiFont = font });
            else SaveAppearance(current => current with { CodeFont = font });
        }

        private async void OnAppearanceExportClick(object sender, RoutedEventArgs e)
        {
            if (XamlRoot?.ContentIslandEnvironment?.AppWindowId is not { } windowId) return;
            try
            {
                var picker = new FileSavePicker(windowId) { SuggestedFileName = "ru-theme" };
                picker.FileTypeChoices.Add("Ru theme", [".json"]);
                var path = (await picker.PickSaveFileAsync())?.Path;
                if (string.IsNullOrEmpty(path)) return;
                await File.WriteAllTextAsync(path, AppearancePreferenceStore.Export(Appearance.Current));
                ShowAppearanceFileStatus($"Theme saved to {Path.GetFileName(path)}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                ShowAppearanceFileStatus("Ru couldn't save the theme file. Try another folder.");
            }
        }

        private async void OnAppearanceImportClick(object sender, RoutedEventArgs e)
        {
            if (XamlRoot?.ContentIslandEnvironment?.AppWindowId is not { } windowId) return;
            try
            {
                var picker = new FileOpenPicker(windowId);
                picker.FileTypeFilter.Add(".json");
                var path = (await picker.PickSingleFileAsync())?.Path;
                if (string.IsNullOrEmpty(path)) return;
                var info = new FileInfo(path);
                var imported = info.Length <= PreferenceFile.MaximumBytes
                    ? AppearancePreferenceStore.Import(await File.ReadAllBytesAsync(path), Appearance.Current)
                    : null;
                if (imported is null)
                {
                    ShowAppearanceFileStatus("That file isn't a Ru theme.");
                    return;
                }

                SaveAppearance(_ => imported);
                ShowAppearanceFileStatus($"Theme loaded from {Path.GetFileName(path)}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                ShowAppearanceFileStatus("Ru couldn't read that file.");
            }
        }

        private void OnAppearanceResetClick(object sender, RoutedEventArgs e)
        {
            HideAppearanceError();
            if (!Appearance.Reset()) ShowAppearanceFileStatus("Appearance was reset, but Ru couldn't save it.");
            else ShowAppearanceFileStatus("Appearance reset.");
        }

        private void SaveAppearance(Func<AppearancePreferences, AppearancePreferences> change)
        {
            if (!Appearance.Update(change)) ShowAppearanceError("Ru couldn't save your appearance settings.");
        }

        private void ShowAppearanceError(string message)
        {
            AppearanceBackgroundError.Text = message;
            AppearanceBackgroundError.Visibility = Visibility.Visible;
        }

        private void HideAppearanceError() => AppearanceBackgroundError.Visibility = Visibility.Collapsed;

        private void ShowAppearanceFileStatus(string message)
        {
            AppearanceFileStatus.Text = message;
            AppearanceFileStatus.Visibility = Visibility.Visible;
        }

        private static void SelectByTag(ComboBox box, string tag) =>
            box.SelectedItem = box.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal));

        private static string? SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;
    }
}
