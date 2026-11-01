using RuAgent.Cli.Chat;
using Terminal.Gui.App;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Native input bindings over the shared conversation engine.</summary>
public sealed class TgChatLoop : ChatSessionEngine
{
    private readonly TgChatKeyboardBindings _keyboardBindings;
    private readonly TranscriptStream _stream;

    public TgChatLoop(TgChatWindow window, CancellationToken cancellationToken = default)
        : this(window, new TgChatEngineView(window), cancellationToken) { }

    private TgChatLoop(TgChatWindow window, TgChatEngineView view, CancellationToken ct) : base(view, ct)
    {
        _stream = new TranscriptStream(window.Transcript, view.Dispatch);
        _keyboardBindings = new TgChatKeyboardBindings(window, () => IsBusy, () => HasCancelableWork,
            Submit, Cancel, NavigateHistory, CompleteCommand, Paste, Copy);
        window.IsRunningChanged += (_, args) => { if (!args.Value) Stop(); };
    }

    internal void SetViewActive(IApplication? app) => _keyboardBindings.SetApplicationRouting(app);

    public void SetPromptHandler(Func<string, TranscriptStream, CancellationToken, Task> handler) =>
        base.SetPromptHandler((line, ct) => handler(line, _stream, ct));

    internal void SetSubmissionHandler(Func<ChatSubmissionSnapshot, TranscriptStream, CancellationToken, Task> handler) =>
        base.SetSubmissionHandler((submission, ct) => handler(submission, _stream, ct));
}

/// <summary>Native append facade for callers that explicitly draw transcript rows.</summary>
public sealed class TranscriptStream(TgTranscriptView transcript, Action<Action> dispatch)
{
    public void Append(string text, TranscriptStyle style) => dispatch(() => transcript.Append(text, style));
    public void AppendChunk(string text) => dispatch(() => transcript.AppendChunk(text));
}
