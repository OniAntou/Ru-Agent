using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RuApp.Views
{
    public sealed partial class SettingsView
    {
        public bool HasOpenComboBox => UpdateChannelCombo.IsDropDownOpen;

        public void DismissFlyouts()
        {
            ThemeButton.Flyout?.Hide();
            PresetButton.Flyout?.Hide();
            ProviderButton.Flyout?.Hide();
            UpdateChannelCombo.IsDropDownOpen = false;
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

        private enum SettingsTab { General, Models, Rules, Shortcuts, About }

        private void OnNavRulesClick(object sender, RoutedEventArgs e)
        {
            SelectTab(SettingsTab.Rules);
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

        private void SelectTab(SettingsTab tab)
        {
            PanelGeneralScrollViewer.Visibility = tab == SettingsTab.General ? Visibility.Visible : Visibility.Collapsed;
            PanelModelsScrollViewer.Visibility = tab == SettingsTab.Models ? Visibility.Visible : Visibility.Collapsed;
            PanelRulesScrollViewer.Visibility = tab == SettingsTab.Rules ? Visibility.Visible : Visibility.Collapsed;
            PanelShortcutsScrollViewer.Visibility = tab == SettingsTab.Shortcuts ? Visibility.Visible : Visibility.Collapsed;
            PanelAboutScrollViewer.Visibility = tab == SettingsTab.About ? Visibility.Visible : Visibility.Collapsed;
            var (title, subtitle, active) = tab switch
            {
                SettingsTab.General => ("General", "Manage application theme and agent defaults.", NavGeneralBtn),
                SettingsTab.Models => ("Models", "Connect providers and manage access to their models.", NavModelsBtn),
                SettingsTab.Rules => ("Approval rules", "Review and remove the approvals Ru remembers.", NavRulesBtn),
                SettingsTab.Shortcuts => ("Keyboard shortcuts", "Every shortcut Ru binds. Ctrl+K opens the command palette.", NavShortcutsBtn),
                _ => ("About", "Application information and updates.", NavAboutBtn),
            };
            CategoryTitleText.Text = title;
            CategorySubtitleText.Text = subtitle;
            foreach (var button in new[] { NavGeneralBtn, NavModelsBtn, NavRulesBtn, NavShortcutsBtn, NavAboutBtn })
                HighlightNavButton(button, button == active);
        }

        private static void HighlightNavButton(Button button, bool active)
        {
            button.Background = active
                ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuLineBrush"]
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[active ? "RuInkBrush" : "RuMutedBrush"];
            button.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }
}
