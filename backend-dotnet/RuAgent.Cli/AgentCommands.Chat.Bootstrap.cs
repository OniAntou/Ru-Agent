using System.IO;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    /// <summary>
    /// ChatAsync's bootstrap: preset/workspace/session validation, the settings
    /// read that names the run's provider, presentation selection, the Terminal.Gui
    /// frame construction and the attachment intake. Everything ChatAsync's run
    /// loop assumes is established here; failure paths drain what was created
    /// so a refused chat cannot leak the frame or delete-tasks of the images it
    /// already loaded.
    /// </summary>
    internal sealed class ChatBootstrapContext(
        ChatSubmissionState submissions,
        TextWriter humanOut,
        string canonicalWorkspace,
        string workspaceLabel,
        string sessionId,
        string backendName,
        string activeModel,
        string reasoningEffort,
        string? modelWarning,
        ChatPresentationMode presentation,
        bool tgMode,
        TgTranscriptWriter? tgWriter,
        TgChatWindow? tgWindow,
        IApplication? tgApp) : IAsyncDisposable
    {
        public ChatSubmissionState Submissions { get; } = submissions;
        public TextWriter HumanOut { get; } = humanOut;
        public string CanonicalWorkspace { get; } = canonicalWorkspace;
        public string WorkspaceLabel { get; } = workspaceLabel;
        public string SessionId { get; } = sessionId;
        public string BackendName { get; } = backendName;
        public string ActiveModel { get; } = activeModel;
        public string ReasoningEffort { get; } = reasoningEffort;
        public string? ModelWarning { get; } = modelWarning;
        public ChatPresentationMode Presentation { get; } = presentation;
        public bool TgMode { get; } = tgMode;
        public TgTranscriptWriter? TgWriter { get; } = tgWriter;
        public TgChatWindow? TgWindow { get; } = tgWindow;
        public IApplication? TgApp { get; } = tgApp;

        public ValueTask DisposeAsync() => DisposeChatBootstrapAsync(Submissions, TgWindow, TgApp);
    }

    /// <summary>
    /// The shared teardown order: submissions first (image temp files release
    /// without a window), then the window, then the app. The transcript writer
    /// is deliberately not disposed: it never was, and closing its TextWriter
    /// would turn a benign write into a stopped frame into an exception inside
    /// an in-flight turn.
    /// </summary>
    private static ValueTask DisposeChatBootstrapAsync(
        ChatSubmissionState submissions,
        TgChatWindow? tgWindow,
        IApplication? tgApp)
    {
        submissions.Dispose();
        tgWindow?.Dispose();
        tgApp?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<ChatBootstrapContext?> PrepareChatAsync(ChatCommand cmd, CancellationToken cancellationToken)
    {
        // Preset validation and canonicalization live on the caller: ChatAsync
        // owns the single cmd instance every downstream consumer closes over.
        string canonicalWorkspace;
        try
        {
            canonicalWorkspace = Path.GetFullPath(cmd.WorkspacePath);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"invalid_workspace: {ex.Message}").ConfigureAwait(false);
            return null;
        }

        string sessionId = cmd.SessionId ?? string.Empty;
        // No durable session for an unused welcome screen or local slash commands.
        // The first real prompt materializes it; /new remains an explicit creation.

        // The TUI header reports the provider the run is using. When settings cannot
        // be read the header says so rather than naming a provider the run never
        // picked: a substituted value here is indistinguishable from a real one.
        string? backendName = null;
        string activeModel = string.Empty;
        string reasoningEffort = string.Empty;
        try
        {
            var exePath = Environment.ProcessPath;
            var exeStamp = exePath is not null && File.Exists(exePath)
                ? File.GetLastWriteTimeUtc(exePath).ToString("O")
                : "unknown";
            Tui.TuiDebug.Log("build", $"exe={exePath} stamp={exeStamp}");
        }
        catch (Exception)
        {
            // Build stamp is diagnostics-only.
        }
        string? modelWarning = null;
        try
        {
            var settings = await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
            backendName = settings.TryGetProperty("backend", out var b) && !string.IsNullOrWhiteSpace(b.GetString())
                ? b.GetString()
                : null;
            activeModel = backendName switch
            {
                "OpenAI" => settings.TryGetProperty("openai_model", out var oam) ? oam.GetString() ?? "" : "",
                "Anthropic" => settings.TryGetProperty("anthropic_model", out var anm) ? anm.GetString() ?? "" : "",
                _ => settings.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
            };
            reasoningEffort = ReadReasoningEffort(settings);
        }
        catch (Exception)
        {
            // Settings backend unreachable; the header reports it as unknown.
        }

        // Presentation selection happens once settings are known: --json, --classic,
        // redirected I/O, NO_COLOR, a dumb TERM and a small startup viewport use line I/O. In TG
        // mode the window and its transcript writer are created before the session renderer
        // so the shared run machinery renders into the Terminal.Gui transcript, never stdout.
        var presentation = ChatPresentationSelection.Resolve(
            Console.IsOutputRedirected,
            cmd.Json,
            Tui.TuiViewport.Width(),
            Tui.TuiViewport.Height(),
            Environment.GetEnvironmentVariable("NO_COLOR"),
            Environment.GetEnvironmentVariable("TERM"),
            cmd.Classic,
            Console.IsInputRedirected || !ReferenceEquals(_in, Console.In));
        bool tgMode = presentation == ChatPresentationMode.Native;
        IApplication? tgApp = tgMode ? Application.Create() : null;
        if (tgApp is not null)
        {
            TgTerminalInput.Initialize(tgApp);
        }

        ChatSubmissionState? submissions = null;
        TgChatWindow? tgWindow = null;
        try
        {
            tgWindow = tgMode ? TgChatWindow.Build() : null;
            if (tgApp is not null)
            {
                TgTerminalPaste.Attach(tgApp);
            }

            TgTranscriptWriter? tgWriter = tgMode ? new TgTranscriptWriter(tgApp!, tgWindow!.Transcript) : null;

            if (backendName is not null)
            {
                // Best-effort and once per chat: a models.dev id that is not in the catalog
                // would otherwise only surface mid-turn as model_unavailable.
                modelWarning = await ModelPrecheck
                    .CheckAsync(_settingsClient, backendName, activeModel, cancellationToken)
                    .ConfigureAwait(false);
            }

            backendName ??= "(provider unknown)";

            // --json owns stdout: every human-facing line a command prints goes to
            // stderr instead, so a consumer can parse stdout line by line as NDJSON.
            // Terminal.Gui owns the screen: human lines land in the transcript view.
            var humanOut = cmd.Json ? _err : (TextWriter?)tgWriter ?? _out;

            var ws = Tui.TuiHeader.ShortenWorkspace(canonicalWorkspace, 48);
            var workspaceLabel = ws;

            submissions = new ChatSubmissionState();
            if (cmd.Images is { Count: > 0 })
            {
                foreach (var image in cmd.Images)
                {
                    var (attachment, imageError) = await Images.CliImageFiles
                        .LoadAsync(image, canonicalWorkspace, cancellationToken)
                        .ConfigureAwait(false);
                    if (attachment is null)
                    {
                        await _err.WriteLineAsync(imageError).ConfigureAwait(false);
                        await DisposeChatBootstrapAsync(submissions, tgWindow, tgApp).ConfigureAwait(false);
                        return null;
                    }

                    submissions.Add(attachment);
                }
            }

            if (!cmd.Json && !tgMode)
                await humanOut.WriteLineAsync($"Ru Agent · {ChatDisplayText.Sanitize(workspaceLabel, 64)}").ConfigureAwait(false);

            return new ChatBootstrapContext(
                submissions,
                humanOut,
                canonicalWorkspace,
                workspaceLabel,
                sessionId,
                backendName,
                activeModel,
                reasoningEffort,
                modelWarning,
                presentation,
                tgMode,
                tgWriter,
                tgWindow,
                tgApp);
        }
        catch
        {
            // The prologue's locals used to unwind through `using` scopes on
            // any escape mid-bootstrap; mirror that drain for the gathered
            // pieces before rethrowing.
            if (submissions is not null)
            {
                await DisposeChatBootstrapAsync(submissions, tgWindow, tgApp).ConfigureAwait(false);
            }
            else
            {
                tgWindow?.Dispose();
                tgApp?.Dispose();
            }

            throw;
        }
    }
}
