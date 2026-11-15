using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RuApp.Services;

namespace RuApp.Views
{
    /// <summary>
    /// The Settings search box. A page made of cards filters its cards; a page that
    /// is one long list (providers, shortcuts, updates) answers to its keywords and
    /// stays whole. The tabs with no match drop out of the rail, and the open page
    /// moves to the first one that matches.
    /// </summary>
    public sealed partial class SettingsView
    {
        private sealed record SearchEntry(SettingsTab Tab, FrameworkElement? Card, string Text);

        private static readonly (SettingsTab Tab, string Keywords)[] PageKeywords =
        [
            (SettingsTab.Models, "models providers provider api key credential connect catalog custom endpoint"),
            (SettingsTab.Shortcuts, "keyboard shortcuts keys hotkeys command palette"),
            (SettingsTab.About, "about updates update version release channel build github install"),
        ];

        private List<SearchEntry>? _searchIndex;
        private bool _searchHidPanels;

        private void OnSettingsSearchChanged(object sender, TextChangedEventArgs e) =>
            ApplySettingsSearch(SettingsSearchBox.Text);

        private void OnSettingsSearchKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Escape || SettingsSearchBox.Text.Length == 0) return;
            SettingsSearchBox.Text = string.Empty;
            e.Handled = true;
        }

        private List<SearchEntry> BuildSearchIndex()
        {
            var entries = new List<SearchEntry>();
            foreach (var (tab, panel) in new[]
                     {
                         (SettingsTab.General, PanelGeneralScrollViewer),
                         (SettingsTab.Appearance, PanelAppearanceScrollViewer),
                         (SettingsTab.Permissions, PanelRulesScrollViewer),
                     })
            {
                if (panel.Content is not Panel cards) continue;
                foreach (var card in cards.Children.OfType<FrameworkElement>())
                    entries.Add(new SearchEntry(tab, card, CollectText(card)));
            }

            foreach (var (tab, keywords) in PageKeywords)
                entries.Add(new SearchEntry(tab, null, keywords));
            return entries;
        }

        /// <summary>Every label, description and choice a card shows, as one searchable string.</summary>
        private static string CollectText(FrameworkElement card)
        {
            var parts = new List<string>();
            foreach (var element in SettingsDescendants(card).Prepend(card))
            {
                switch (element)
                {
                    case TextBlock { Text.Length: > 0 } text:
                        parts.Add(text.Text);
                        break;
                    case ContentControl { Content: string content } when content.Length > 0:
                        parts.Add(content);
                        break;
                }
            }

            return string.Join(' ', parts);
        }

        private void ApplySettingsSearch(string? query)
        {
            var words = SettingsSearch.Words(query);
            bool filtering = words.Length > 0;
            _searchIndex ??= BuildSearchIndex();

            var tabMatches = new Dictionary<SettingsTab, bool>();
            foreach (var entry in _searchIndex)
            {
                bool matches = SettingsSearch.Matches(query, entry.Text);
                if (entry.Card is not null) UiMotion.SetVisible(entry.Card, matches);
                tabMatches[entry.Tab] = tabMatches.GetValueOrDefault(entry.Tab) || matches;
            }

            bool Shown(SettingsTab tab) => !filtering || tabMatches.GetValueOrDefault(tab);
            SetNavVisible(NavGeneralBtn, Shown(SettingsTab.General));
            SetNavVisible(NavAppearanceBtn, Shown(SettingsTab.Appearance));
            SetNavVisible(NavModelsBtn, Shown(SettingsTab.Models));
            SetNavVisible(NavRulesBtn, Shown(SettingsTab.Permissions));
            SetNavVisible(NavShortcutsBtn, Shown(SettingsTab.Shortcuts));
            SetNavVisible(NavAboutBtn, Shown(SettingsTab.About));
            UpdateRuHeadingVisibility();

            var first = new[] { SettingsTab.General, SettingsTab.Appearance, SettingsTab.Models, SettingsTab.Permissions, SettingsTab.Shortcuts, SettingsTab.About }
                .Cast<SettingsTab?>()
                .FirstOrDefault(tab => Shown(tab!.Value));
            bool noResults = filtering && first is null;
            UiMotion.SetVisible(SettingsNoResultsText, noResults);
            if (noResults)
            {
                SettingsNoResultsText.Text = $"No settings match “{string.Join(' ', words)}”.";
                _searchHidPanels = true;
                foreach (var panel in AllPanels) panel.Visibility = Visibility.Collapsed;
                return;
            }

            var current = _shownTab ?? SettingsTab.General;
            if (filtering && !Shown(current))
            {
                NavigateTo(first!.Value);
            }
            else if (_searchHidPanels)
            {
                _searchHidPanels = false;
                // The panels were hidden for an empty result; bring the open one back.
                _shownTab = null;
                SelectTab(current);
            }
        }

        private ScrollViewer[] AllPanels =>
            [PanelGeneralScrollViewer, PanelAppearanceScrollViewer, PanelModelsScrollViewer, PanelRulesScrollViewer, PanelShortcutsScrollViewer, PanelAboutScrollViewer];

        private static void SetNavVisible(Button button, bool visible) =>
            button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>"Ru" heads only the About page, so it leaves with it and in the narrow strip.</summary>
        private void UpdateRuHeadingVisibility() =>
            SettingsNavigationRuHeading.Visibility =
                _settingsNarrow != true && NavAboutBtn.Visibility == Visibility.Visible
                    ? Visibility.Visible
                    : Visibility.Collapsed;

        private void NavigateTo(SettingsTab tab)
        {
            switch (tab)
            {
                case SettingsTab.General: OnNavGeneralClick(this, new RoutedEventArgs()); break;
                case SettingsTab.Appearance: OnNavAppearanceClick(this, new RoutedEventArgs()); break;
                case SettingsTab.Models: OnNavModelsClick(this, new RoutedEventArgs()); break;
                case SettingsTab.Permissions: OnNavRulesClick(this, new RoutedEventArgs()); break;
                case SettingsTab.Shortcuts: OnNavShortcutsClick(this, new RoutedEventArgs()); break;
                default: OnNavAboutClick(this, new RoutedEventArgs()); break;
            }
        }
    }
}
