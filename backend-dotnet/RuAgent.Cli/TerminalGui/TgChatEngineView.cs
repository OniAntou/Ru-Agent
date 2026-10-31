using RuAgent.Cli.Chat;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Terminal.Gui presentation of controller state; owns no conversation worker.</summary>
internal sealed class TgChatEngineView(TgChatWindow window) : IChatSessionView
{
    public string InputText { get => window.Composer.Input.Text ?? string.Empty; set => window.Composer.Input.Text = value; }
    public int InsertionPoint { get => window.Composer.Input.InsertionPoint; set => window.Composer.Input.InsertionPoint = value; }
    public string Workspace => window.Hints.Workspace;
    public void ShowFeedback(string message, bool isError = false) => window.Hints.ShowFeedback(message, isError);
    public void SetImages(int pending, int held) => window.Composer.SetImages(pending, held);
    public void SetRunning(int queued) => window.Hints.SetRunning(queued);
    public void SetQueueCount(int queued) => window.Hints.SetQueueCount(queued);
    public void SetPaused(int queued) => window.Hints.SetPaused(queued);
    public void SetIdle(string workspace) => window.Hints.SetIdle(workspace);
    public void ClearLive() => window.Transcript.ClearLive();
    public string CopyText() => window.Transcript.CopyText();
    public void Append(string text, ChatMessageKind kind) => window.Transcript.Append(text, kind switch
    {
        ChatMessageKind.User => TranscriptStyle.User,
        ChatMessageKind.Meta => TranscriptStyle.Meta,
        ChatMessageKind.Error => TranscriptStyle.Error,
        _ => TranscriptStyle.Prose,
    });

    public void EchoPrompt(string prompt, bool localCommand)
    {
        if (localCommand) return;
        window.Transcript.Append(string.Empty, TranscriptStyle.User);
        var lines = prompt.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
            window.Transcript.Append((index == 0 ? "  ❯ " : "    ") + TuiText.SanitizeUntrusted(lines[index]), TranscriptStyle.User);
        window.Transcript.Append(string.Empty, TranscriptStyle.User);
    }

    public void Dispatch(Action action)
    {
        var app = window.App;
        if (app is null) { action(); return; }
        try { app.Invoke(action); }
        catch (NotInitializedException) { }
    }
}
