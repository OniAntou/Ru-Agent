using Microsoft.UI.Xaml.Controls;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The "Sending while Ru is working" card: whether Enter steers the working run (the
/// default) or queues the message as the next turn (ADR 0056).
/// </summary>
public sealed partial class SettingsView
{
    private bool _loadingComposerSendPreferences;

    private void LoadComposerSendPreferences()
    {
        var preferences = ComposerSendPreferenceStore.Shared.Load();
        _loadingComposerSendPreferences = true;
        try
        {
            WhileRunningBox.SelectedIndex = preferences.WhileRunning == ComposerWhileRunning.Queue ? 1 : 0;
        }
        finally
        {
            _loadingComposerSendPreferences = false;
        }
    }

    private void OnWhileRunningChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingComposerSendPreferences) return;
        var choice = (WhileRunningBox.SelectedItem as ComboBoxItem)?.Tag as string == nameof(ComposerWhileRunning.Queue)
            ? ComposerWhileRunning.Queue
            : ComposerWhileRunning.Steer;
        if (!ComposerSendPreferenceStore.Shared.TrySave(new ComposerSendPreferences(choice)))
            GuiDiagnosticWriter.AppendTraceLine("[Settings] Composer send preference was not saved.");
    }
}
