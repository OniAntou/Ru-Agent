using System.ComponentModel;
using Microsoft.UI.Xaml;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views
{
    /// <summary>
    /// The transcript's density toggle. Density is a view concern painted over
    /// rows every path shares -- live, replayed, or restored -- so one helper
    /// walks all of them whenever it changes.
    /// </summary>
    public sealed partial class ChatView
    {
        /// <summary>
        /// The density a row is created with, so a session opened in compact mode
        /// does not flash roomy rows before the first pass paints them.
        /// </summary>
        private static void ApplySavedDensity(AgentActivityItem item) =>
            item.FeedCompact = FeedDensity.Instance.IsCompact;

        private void OnFeedDensityChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(FeedDensity.IsCompact))
            {
                return;
            }

            // Density can change from a settings surface on another thread, and the
            // row collections it walks are bound to the UI thread.
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(ApplyFeedDensityToItems);
                return;
            }

            ApplyFeedDensityToItems();
        }

        private void ApplyFeedDensityToItems()
        {
            bool compact = FeedDensity.Instance.IsCompact;

            foreach (var item in AgentActivities)
            {
                item.FeedCompact = compact;
            }

            foreach (var message in Messages)
            {
                if (message.AgentActivities is null)
                {
                    continue;
                }

                foreach (var item in message.AgentActivities)
                {
                    item.FeedCompact = compact;
                }
            }

            if (DensityToggleButton is not null)
            {
                DensityToggleButton.IsChecked = compact;
            }

            if (DensityToggleLabel is not null)
            {
                DensityToggleLabel.Text = compact ? "Compact" : "Roomy";
            }
        }

        /// <summary>
        /// Toggles feed density. The click moves the toggle's own checked state as
        /// well as the preference, so the rows are repainted here instead of
        /// waiting on the property change that the click may have already made.
        /// </summary>
        private void OnDensityToggleClick(object sender, RoutedEventArgs e)
        {
            FeedDensity.Instance.IsCompact = !FeedDensity.Instance.IsCompact;
            ApplyFeedDensityToItems();
        }
    }
}