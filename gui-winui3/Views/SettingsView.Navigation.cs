using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Services;

namespace RuApp.Views
{
    public sealed partial class SettingsView
    {
        public void DismissFlyouts()
        {
            ProviderButton.Flyout?.Hide();
        }

        private void InitializeSettingsHoverEffects()
        {
            SelectTab(SettingsTab.General);
        }

        private void OnNavGeneralClick(object sender, RoutedEventArgs e)
        {
            SelectTab(SettingsTab.General);
        }

        private void OnNavModelsClick(object sender, RoutedEventArgs e)
        {
            SelectTab(SettingsTab.Models);
            _ = ViewModel.RefreshCatalogAsync();
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

        private enum SettingsTab { General, Models, Permissions, Shortcuts, About }

        private void OnNavRulesClick(object sender, RoutedEventArgs e)
        {
            SelectTab(SettingsTab.Permissions);
            _ = Rules.RefreshAsync();
        }

        private void OnNavShortcutsClick(object sender, RoutedEventArgs e)
        {
            ShortcutsListHost.Content ??= KeyboardShortcutList.Build();
            SelectTab(SettingsTab.Shortcuts);
        }

        private void OnRulesRefreshClick(object sender, RoutedEventArgs e) => _ = Rules.RefreshAsync();

        private void OnRemoveRuleClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: ViewModels.ApprovalRuleRow row }) _ = Rules.RemoveCommand.ExecuteAsync(row);
        }

        private void OnNavAboutClick(object sender, RoutedEventArgs e)
        {
            SelectTab(SettingsTab.About);
            CheckForUpdates();
        }

        private SettingsTab? _shownTab;

        private void SelectTab(SettingsTab tab)
        {
            // Only a real change plays the entrance: the first call is the view being
            // built, and a click on the tab already open must not replay it.
            bool animate = _shownTab is { } previous && previous != tab && IsLoaded;
            _shownTab = tab;
            PanelGeneralScrollViewer.Visibility = tab == SettingsTab.General ? Visibility.Visible : Visibility.Collapsed;
            PanelModelsScrollViewer.Visibility = tab == SettingsTab.Models ? Visibility.Visible : Visibility.Collapsed;
            PanelRulesScrollViewer.Visibility = tab == SettingsTab.Permissions ? Visibility.Visible : Visibility.Collapsed;
            PanelShortcutsScrollViewer.Visibility = tab == SettingsTab.Shortcuts ? Visibility.Visible : Visibility.Collapsed;
            PanelAboutScrollViewer.Visibility = tab == SettingsTab.About ? Visibility.Visible : Visibility.Collapsed;
            var (title, subtitle, active) = tab switch
            {
                SettingsTab.General => ("General", "Appearance, reading size, and notifications.", NavGeneralBtn),
                SettingsTab.Models => ("Models", "Connect providers and manage access to their models.", NavModelsBtn),
                SettingsTab.Permissions => ("Permissions", "What Ru can do without asking, and the approvals it remembers.", NavRulesBtn),
                SettingsTab.Shortcuts => ("Keyboard shortcuts", "Every shortcut Ru binds. Ctrl+K opens the command palette.", NavShortcutsBtn),
                _ => ("About", "Application information and updates.", NavAboutBtn),
            };
            CategoryTitleText.Text = title;
            CategorySubtitleText.Text = subtitle;
            foreach (var button in new[] { NavGeneralBtn, NavModelsBtn, NavRulesBtn, NavShortcutsBtn, NavAboutBtn })
                HighlightNavButton(button, button == active, animate);
            if (animate) PlayTabEntrance(tab);
        }

        private static void HighlightNavButton(Button button, bool active, bool animate)
        {
            var resources = Application.Current.Resources;
            var background = active
                ? (Microsoft.UI.Xaml.Media.Brush)resources["RuLineBrush"]
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var foreground = (Microsoft.UI.Xaml.Media.Brush)resources[active ? "RuInkBrush" : "RuMutedBrush"];
            if (animate)
            {
                UiMotion.FadeBrush(button, Control.BackgroundProperty, background);
                UiMotion.FadeBrush(button, Control.ForegroundProperty, foreground);
            }
            else
            {
                button.Background = background;
                button.Foreground = foreground;
            }

            button.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }
}
