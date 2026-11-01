using RuAgent.Cli.Tui;

namespace RuAgent.Cli.Chat;

/// <summary>Data-only I/O state. Human diagnostics never share JSON stdout.</summary>
internal sealed class PlainChatView(TextWriter diagnostics, string workspace) : IChatSessionView
{
    public string InputText { get; set; } = string.Empty;
    public int InsertionPoint { get; set; }
    public string Workspace => workspace;
    public void Dispatch(Action action) => action();
    public void ShowFeedback(string message, bool isError = false) => diagnostics.WriteLine(TuiText.SanitizeUntrusted(message));
    public void Append(string text, ChatMessageKind kind) => diagnostics.WriteLine(TuiText.SanitizeUntrusted(text));
    public void EchoPrompt(string prompt, bool localCommand) { } // Existing text projection owns its prompt echo.
    public void SetImages(int pending, int held) { }
    public void SetRunning(int queued) { }
    public void SetQueueCount(int queued) { }
    public void SetPaused(int queued) { }
    public void SetIdle(string label) { }
    public void ClearLive() { }
    public string CopyText() => string.Empty;
}
