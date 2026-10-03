using RuAgent.Cli.Tui;
using Terminal.Gui.App;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// Marshals the slash-command picker and masked-secret panels onto the Terminal.Gui loop
/// and resolves their answer for the awaiting command: the chosen item's value, the typed
/// secret, or null on Escape/cancellation. The panel is always closed on the way out, and
/// a session that already wound down is a no-op instead of an error.
/// </summary>
public static class TgPanelRequests
{
    /// <summary>Runs a <see cref="ChoiceList"/> picker over the transcript.</summary>
    public static Task<string?> RequestChoiceAsync(
        TgChatWindow window,
        ChoiceList list,
        CancellationToken cancellationToken) =>
        RunAsync(
            window,
            () => window.ShowPicker(list.Title, list.Items, list.Selected, list.Footer),
            cancellationToken);

    /// <summary>Runs the masked secret panel; only the bullet count reaches the screen.</summary>
    public static Task<string?> RequestSecretAsync(
        TgChatWindow window,
        string? providerDisplay,
        CancellationToken cancellationToken) =>
        RunAsync(
            window,
            () => window.ShowSecret(providerDisplay ?? "api key"),
            cancellationToken);

    private static async Task<string?> RunAsync(
        TgChatWindow window,
        Action show,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        var decision = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnConfirmed(string value) => decision.TrySetResult(value);
        void OnCancelled() => decision.TrySetResult(null);

        window.App!.Invoke(() =>
        {
            window.PanelConfirmed += OnConfirmed;
            window.PanelCancelled += OnCancelled;
            show();
        });

        using var registration = cancellationToken.Register(() => decision.TrySetResult(null));
        try
        {
            return await decision.Task.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                window.App.Invoke(() =>
                {
                    window.PanelConfirmed -= OnConfirmed;
                    window.PanelCancelled -= OnCancelled;
                    window.HidePanel();
                });
            }
            catch (NotInitializedException)
            {
                // The session wound down while the panel was open; nothing left to close.
            }
        }
    }
}
