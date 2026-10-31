using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RuApp.Views
{
    /// <summary>The Verbose/Compact choice for how agent activity reads in the transcript.</summary>
    public sealed partial class SettingsView
    {
        private void InitializeActivityStyleChoice()
        {
            Loaded += (_, _) =>
            {
                ActivityFeedPreferences.PropertyChanged += OnActivityFeedPreferenceChanged;
                SyncActivityStyleChoice();
            };
            Unloaded += (_, _) => ActivityFeedPreferences.PropertyChanged -= OnActivityFeedPreferenceChanged;
            SyncActivityStyleChoice();
        }

        private void OnAgentActivityStyleClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string style })
                ActivityFeedPreferences.Compact = style == "compact";
            // A click on the part already chosen must not leave it looking unchosen.
            SyncActivityStyleChoice();
        }

        private void OnActivityFeedPreferenceChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Services.AgentActivityDisplayPreferences.Compact))
                DispatchToUi(SyncActivityStyleChoice);
        }

        private void SyncActivityStyleChoice()
        {
            var compact = ActivityFeedPreferences.Compact;
            CompactActivityOption.IsChecked = compact;
            VerboseActivityOption.IsChecked = !compact;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(
                AgentActivityStyleChoice, compact ? "Compact" : "Verbose");
        }
    }
}
