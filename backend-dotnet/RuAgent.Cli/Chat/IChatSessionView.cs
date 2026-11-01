namespace RuAgent.Cli.Chat;

internal enum ChatMessageKind { Prose, User, Meta, Error }

/// <summary>Input/output capabilities used by the sole chat controller; no widget ownership.</summary>
internal interface IChatSessionView
{
    string InputText { get; set; }
    int InsertionPoint { get; set; }
    string Workspace { get; }
    void Dispatch(Action action);
    void ShowFeedback(string message, bool isError = false);
    void SetImages(int pending, int held);
    void SetRunning(int queued);
    void SetQueueCount(int queued);
    void SetPaused(int queued);
    void SetIdle(string workspace);
    void ClearLive();
    void EchoPrompt(string prompt, bool localCommand);
    void Append(string text, ChatMessageKind kind);
    string CopyText();
}
