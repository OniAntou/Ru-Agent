using Microsoft.UI.Xaml;

namespace RuApp.Views;

/// <summary>
/// Entry points for the shell's command palette and shortcuts. Each one calls the
/// same path the chat's own controls use; none of them is a new way to run anything.
/// </summary>
public sealed partial class ChatView
{
    /// <summary>The empty chat's provider-setup link opens Models &amp; Providers.</summary>
    private void OnWelcomeSetupClick(object sender, RoutedEventArgs e) =>
        ProviderSettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Puts the keyboard in the message box.</summary>
    public void FocusComposer()
    {
        ChatInputTextBox?.Focus(FocusState.Keyboard);
    }

    /// <summary>Opens the model picker the composer's model button opens.</summary>
    public void OpenModelPicker()
    {
        if (ChatModelButton is null || ChatModelFlyout is null) return;
        ChatModelFlyout.ShowAt(ChatModelButton);
    }

    /// <summary>Whether Stop would stop something: the composer shows Stop for the same state.</summary>
    public bool CanStopRun => _isSendButtonInStopMode || IsActiveAgentRunPending();

    /// <summary>Stops the run the same way Stop and Escape do.</summary>
    public async Task StopRunAsync()
    {
        if (!CanStopRun) return;
        await StopActiveAgentAsync();
    }
}
