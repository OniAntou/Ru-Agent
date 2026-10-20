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

        private enum SettingsTab { General, Models, About }

        private void OnNavAboutClick(object sender, RoutedEventArgs e)
        {
            SelectTab(SettingsTab.About);
            CheckForUpdates();
        }

        private void SelectTab(SettingsTab tab)
        {
            PanelGeneralScrollViewer.Visibility = tab == SettingsTab.General ? Visibility.Visible : Visibility.Collapsed;
            PanelModelsScrollViewer.Visibility = tab == SettingsTab.Models ? Visibility.Visible : Visibility.Collapsed;
            PanelAboutScrollViewer.Visibility = tab == SettingsTab.About ? Visibility.Visible : Visibility.Collapsed;
            var (title, subtitle, active) = tab switch
            {
                SettingsTab.General => ("General", "Manage application theme and agent defaults.", NavGeneralBtn),
                SettingsTab.Models => ("Models & Providers", "Configure cloud AI model providers, API credentials, and server connections.", NavModelsBtn),
                _ => ("About", "Application information and updates.", NavAboutBtn),
            };
            CategoryTitleText.Text = title;
            CategorySubtitleText.Text = subtitle;
            foreach (var button in new[] { NavGeneralBtn, NavModelsBtn, NavAboutBtn })
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
