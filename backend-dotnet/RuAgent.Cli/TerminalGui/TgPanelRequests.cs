using RuAgent.Cli.Tui;
using Terminal.Gui.App;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// Marshals the slash-command picker and masked-secret panels onto the Terminal.Gui loop
/// and resolves their answer for the awaiting command: the chosen item's value, the typed
/// secret, or null on Escape/cancellation. The session picker additionally reports a delete
/// request for the row under the cursor. The panel is always closed on the way out, and
/// a session that already wound down is a no-op instead of an error.
/// </summary>
public static class TgPanelRequests
{
    internal static Task<string?> RequestQuestionAsync(TgChatWindow window, ChatInputQuestion question,
        CancellationToken cancellationToken) => RunAsync(window,
            () => window.ShowQuestion(question), value => value, delete: null, cancelled: null, cancellationToken);

    /// <summary>Runs a <see cref="ChoiceList"/> picker over the transcript.</summary>
    public static Task<string?> RequestChoiceAsync(
        TgChatWindow window,
        ChoiceList list,
        CancellationToken cancellationToken) =>
        RunAsync(
            window,
            () => window.ShowPicker(list.Title, list.Items, list.Selected, list.Footer),
            confirmed: value => value,
            delete: null,
            cancelled: null,
            cancellationToken);

    /// <summary>
    /// Runs the session picker: the answer is the chosen session, or a delete request for the
    /// row under the cursor (Delete or Ctrl+D). Escape answers neither.
    /// </summary>
    internal static Task<SessionPickerOutcome> RequestSessionChoiceAsync(
        TgChatWindow window,
        ChoiceList list,
        CancellationToken cancellationToken) =>
        RunAsync(
            window,
            () => window.ShowPicker(list.Title, list.Items, list.Selected, list.Footer, allowDelete: true),
            confirmed: value => new SessionPickerOutcome(value, DeleteRequested: false),
            delete: value => new SessionPickerOutcome(value, DeleteRequested: true),
            cancelled: new SessionPickerOutcome(null, DeleteRequested: false),
            cancellationToken);

    /// <summary>Runs the masked secret panel; only the bullet count reaches the screen.</summary>
    public static Task<string?> RequestSecretAsync(
        TgChatWindow window,
        string? providerDisplay,
        CancellationToken cancellationToken) =>
        RunAsync(
            window,
            () => window.ShowSecret(providerDisplay ?? "api key"),
            confirmed: value => value,
            delete: null,
            cancelled: null,
            cancellationToken);

    /// <summary>
    /// Opens the read-only output window and completes when the operator closes it with Enter
    /// or Escape. The rows are only borrowed for the window: the caller keeps printing them to
    /// the transcript, so nothing is lost when it closes.
    /// </summary>
    public static async Task RequestDocumentAsync(
        TgChatWindow window,
        string title,
        IReadOnlyList<string> body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(body);
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClosed() => closed.TrySetResult(true);

        try
        {
            window.App!.Invoke(() =>
            {
                window.DocumentClosed += OnClosed;
                window.ShowDocument(title, body);
            });
        }
        catch (NotInitializedException)
        {
            // The session wound down before the window could open; there is
            // nothing to show and nothing left to close.
            return;
        }

        using var registration = cancellationToken.Register(() => closed.TrySetResult(true));
        try
        {
            await closed.Task.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                window.App.Invoke(() =>
                {
                    window.DocumentClosed -= OnClosed;
                    window.HideDocument();
                });
            }
            catch (NotInitializedException)
            {
                // The session wound down while the window was open; nothing left to close.
            }
        }
    }

    private static async Task<T> RunAsync<T>(
        TgChatWindow window, Action show, Func<string, T> confirmed, Func<string, T>? delete,
        T cancelled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        try { await window.ModalRequestGate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return cancelled; }
        try { return await RunCoreAsync(window, show, confirmed, delete, cancelled, cancellationToken).ConfigureAwait(false); }
        finally { window.ModalRequestGate.Release(); }
    }

    private static async Task<T> RunCoreAsync<T>(
        TgChatWindow window,
        Action show,
        Func<string, T> confirmed,
        Func<string, T>? delete,
        T cancelled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        var decision = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnConfirmed(string value) => decision.TrySetResult(confirmed(value));
        void OnCancelled() => decision.TrySetResult(cancelled);
        void OnDelete(string value) => decision.TrySetResult(delete is null ? cancelled : delete(value));

        try
        {
            window.App!.Invoke(() =>
            {
                if (decision.Task.IsCompleted) return;
                window.PanelConfirmed += OnConfirmed;
                window.PanelCancelled += OnCancelled;
                if (delete is not null)
                {
                    window.PanelDeleteRequested += OnDelete;
                }

                show();
            });
        }
        catch (NotInitializedException)
        {
            // The session wound down before the panel could open: the answer is
            // a cancel, never a silent confirmation.
            return cancelled;
        }

        using var registration = cancellationToken.Register(() => decision.TrySetResult(cancelled));
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
                    if (delete is not null)
                    {
                        window.PanelDeleteRequested -= OnDelete;
                    }

                    window.HidePanel();
                });
            }
            catch (NotInitializedException)
            {
                // The session wound down while the window was open; nothing left to close.
            }
        }
    }
}
