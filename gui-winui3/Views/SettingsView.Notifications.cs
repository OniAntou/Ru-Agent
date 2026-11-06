using Microsoft.UI.Xaml;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The Notifications card. The window owns delivery and the saved preferences; the
/// card shows them and hands each change back.
/// </summary>
public sealed partial class SettingsView
{
    private bool _loadingNotificationPreferences;

    private void LoadNotificationPreferences()
    {
        var preferences = MainWindow.Instance?.NotificationPreferences ?? new NotificationPreferenceStore().Load();
        _loadingNotificationPreferences = true;
        try
        {
            NotifyFinishedToggle.IsOn = preferences.OnFinished;
            NotifyAttentionToggle.IsOn = preferences.OnNeedsAttention;
            NotifyFlashToggle.IsOn = preferences.FlashTaskbar;
            NotifySoundToggle.IsOn = preferences.PlaySound;
            NotifyToastToggle.IsOn = preferences.ShowToast;
            UpdateNotificationDeliveryState();
        }
        finally
        {
            _loadingNotificationPreferences = false;
        }
    }

    private void OnNotificationToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingNotificationPreferences) return;
        UpdateNotificationDeliveryState();
        var preferences = new NotificationPreferences(
            OnFinished: NotifyFinishedToggle.IsOn,
            OnNeedsAttention: NotifyAttentionToggle.IsOn,
            FlashTaskbar: NotifyFlashToggle.IsOn,
            PlaySound: NotifySoundToggle.IsOn,
            ShowToast: NotifyToastToggle.IsOn);
        bool saved = MainWindow.Instance?.UpdateNotificationPreferences(preferences)
            ?? new NotificationPreferenceStore().TrySave(preferences);
        if (!saved) GuiDiagnosticWriter.AppendTraceLine("[Settings] Notification preferences were not saved.");
    }

    /// <summary>How to notify matters only while some event is turned on.</summary>
    private void UpdateNotificationDeliveryState()
    {
        bool anyEvent = NotifyFinishedToggle.IsOn || NotifyAttentionToggle.IsOn;
        NotifyFlashToggle.IsEnabled = NotifySoundToggle.IsEnabled = NotifyToastToggle.IsEnabled = anyEvent;
    }
}
