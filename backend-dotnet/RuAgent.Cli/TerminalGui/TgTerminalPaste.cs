using Terminal.Gui.App;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// Routes terminal-driven pastes to the composer. Windows Terminal consumes Ctrl+V itself and
/// replays the clipboard as a bracketed paste, which reaches Terminal.Gui as an application-level
/// paste event rather than a key. Terminal.Gui then dispatches Command.Paste to
/// Navigation.GetFocused(), which in this app can be the composer card (TgComposerView) instead of
/// the editor, silently dropping the text; key events route down the Focused chain instead, so the
/// paste payload mirrors that path and lands in the editor that would have received the keys.
/// </summary>
public static class TgTerminalPaste
{
    /// <summary>Subscribes the paste route for the lifetime of <paramref name="app"/>.</summary>
    public static void Attach(IApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Paste += (_, e) =>
        {
            if (e.Handled || string.IsNullOrEmpty(e.Text))
            {
                return;
            }

            var window = app.TopRunnableView as TgChatWindow
                ?? (app.TopRunnableView as TgSessionHostWindow)?.ActivePage;
            if (window is not null)
            {
                e.Handled = window.TryPasteText(e.Text);
            }
            else if (app.TopRunnableView?.MostFocused is TgPromptEditor editor)
            {
                editor.TryInsertDraft(e.Text);
                e.Handled = true;
            }
        };
    }
}
