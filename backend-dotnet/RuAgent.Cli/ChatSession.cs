using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Cli.Attachments;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Terminal.Gui.App;


namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    /// <summary>
    /// One interactive chat: the state the loop shares (window, writers, trackers, the pending
    /// attachments) and the handlers that act on it. It used to be a single method of closures;
    /// the handlers keep their original bodies and live in the <c>ChatSession.*.cs</c> files
    /// by concern: attachments, approvals and trust, header state, run following, session
    /// switching, prompt and slash dispatch, and the Terminal.Gui loop.
    /// </summary>
    private sealed partial class ChatSession
    {
        private readonly AgentCommands _host;
        private ChatCommand cmd;
        private readonly ChatBootstrapContext bootstrap;
        private readonly CancellationToken cancellationToken;

        // Set from the bootstrap context and the startup sequence in RunAsync, in the order the
        // closure declared them.
        private bool tgMode = default!;
        private IApplication? tgApp = default!;
        private TgChatWindow? tgWindow = default!;
        private TgTranscriptWriter? tgWriter = default!;
        private TextWriter humanOut = default!;
        private ChatSubmissionState submissions = default!;
        private string canonicalWorkspace = default!;
        private string sessionId = default!;
        private string workspaceLabel = default!;
        private string backendName = default!;
        private string activeModel = default!;
        private string? modelWarning = default!;
        private IReadOnlyList<Images.CliImageAttachment> pendingImages = default!;
        private IReadOnlyList<CliTextAttachment> pendingTexts = default!;
        private TgChatLoop? tgLoop = null;
        private Tui.TuiSessionRenderer sessionRenderer = default!;
        private Tui.PlainChatInput lineReader = default!;
        private string? sessionTitle = null;
        private long? lastTotalTokens = null;
        private Tui.TuiUsageTracker usage = default!;
        private Subagents.SubagentLiveTracker subagentTracker = default!;
        private ChatModelUsage modelUsage = default!;
        private int sessionTitleProbes = 0;
        private ChatRunSession runSession = default!;
        private int turn = 0;
        private int? dispatcherExitCode = null;
        private ChatSessionCommands sessionCommands = default!;

        public ChatSession(AgentCommands host, ChatCommand cmd, ChatBootstrapContext bootstrap, CancellationToken cancellationToken)
        {
            _host = host;
            this.cmd = cmd;
            this.bootstrap = bootstrap;
            this.cancellationToken = cancellationToken;
        }

        /// <summary>
        /// The transcript renderer writes rows; in --json mode every human row would land on
        /// the NDJSON stream that owns stdout, so the renderer paints into a null writer while
        /// still tracking state (streamed text, tool counts) for recovery.
        /// </summary>
        TextWriter ResolveRenderWriter() => cmd.Json ? TextWriter.Null : tgWriter ?? (TextWriter)_out;

        public async Task<int> RunAsync()
        {
            tgMode = bootstrap.TgMode;
            tgApp = bootstrap.TgApp;
            tgWindow = bootstrap.TgWindow;
            tgWriter = bootstrap.TgWriter;
            humanOut = bootstrap.HumanOut;
            submissions = bootstrap.Submissions;
            canonicalWorkspace = bootstrap.CanonicalWorkspace;
            sessionId = bootstrap.SessionId;
            workspaceLabel = bootstrap.WorkspaceLabel;
            backendName = bootstrap.BackendName;
            activeModel = bootstrap.ActiveModel;
            modelWarning = bootstrap.ModelWarning;
            pendingImages = submissions.Images;
            pendingTexts = submissions.Texts;
            // The loop is created once the run handlers exist (RunTerminalGuiChatAsync);
            // attachment mutations below refresh its hint row through this seam.
            sessionRenderer = CreateSessionRenderer(ResolveRenderWriter());
            if (tgWriter is not null)
            {
                // Ctrl+O parity: a classic row that advertises expansion (`· ctrl+o`) carries
                // its hidden diff/thinking rows in the renderer, not on the wire. The provider
                // lets the bridge hand them to the TG transcript so expansion works end to end.
                tgWriter.RetainedLinesProvider = () => sessionRenderer.Transcript.RetainedLines;
            }

            lineReader = new Tui.PlainChatInput();

            // Header state for the Terminal.Gui header. The title is read from the
            // session document (bounded probes) and the tally repaints as usage frames arrive.
            usage = new Tui.TuiUsageTracker();
            subagentTracker = new Subagents.SubagentLiveTracker();
            modelUsage = new ChatModelUsage(_settingsClient.GetModelDetailsAsync);
            runSession = new ChatRunSession();
            using var runSessionLease = runSession;

            // Slash-command surfaces: the pickers run through the TG panels or the sequential
            // reader. --json owns stdout, so every human-facing line a slash command prints goes
            // to stderr and picker-only commands refuse instead of prompting.
            _slashCommands.HumanOut = humanOut;
            _slashCommands.Json = cmd.Json;
            _slashCommands.PanelWidth = Tui.TuiViewport.Width();
            _slashCommands.FramelessReports = tgMode;
            // The TG frame has no scrollback behind the picker, so a cancelled bare form must
            // reprint the option panel instead of leaving the operator with a silent no-op.
            _slashCommands.ReprintPanelOnPickerCancel = tgMode;
            // Terminal.Gui is the only engine with a floating picker. The sequential
            // fallback leaves Picker null so the command handlers print their option
            // panels (or a bounded interactive_required) instead of calling a no-op.
            if (!cmd.Json && tgMode)
            {
                _slashCommands.Picker = (list, ct) => TgPanelRequests.RequestChoiceAsync(tgWindow!, list, ct);
            }
            else
            {
                _slashCommands.Picker = null;
            }

            _slashCommands.SecretPrompt = (prompt, ct) => cmd.Json
                ? Task.FromResult<string?>(null)
                : tgMode
                    ? TgPanelRequests.RequestSecretAsync(tgWindow!, null, ct)
                    : Tui.ConsoleSecretInput.ReadAsync(_out, prompt, ct);
            _slashCommands.SecretPanelPrompt = (providerDisplay, ct) => cmd.Json
                ? Task.FromResult<string?>(null)
                : tgMode
                    ? TgPanelRequests.RequestSecretAsync(tgWindow!, providerDisplay, ct)
                    : Tui.ConsoleSecretInput.ReadAsync(_out, $"API key for {ChatDisplayText.Sanitize(providerDisplay, 64)}: ", ct);

            _slashCommands.SnapshotProvider = () => new Tui.TuiStatusSnapshot(
                SessionId: sessionId,
                Preset: cmd.Preset,
                Backend: backendName,
                Model: activeModel,
                Workspace: canonicalWorkspace,
                Turn: turn,
                TotalTokens: lastTotalTokens,
                ToolsSettled: sessionRenderer.Transcript.ToolsSettled,
                ApiKeyConfigured: null);
            await RefreshModelSelectionAsync().ConfigureAwait(false);

            if (!cmd.Json && !string.IsNullOrWhiteSpace(cmd.SessionId))
            {
                // A reopened session shows its last messages; the TG frame owns its welcome view.
                var replay = await ChatSessionReplay.LoadAsync(_sessionClient, sessionId, cancellationToken).ConfigureAwait(false);
                if (replay.Title is not null)
                {
                    sessionTitle = replay.Title;
                }
                if (replay.Messages.Length > 0)
                {

                    RenderSessionReplay(sessionId, replay);
                }
                else if (replay.Error is not null)
                {
                    sessionRenderer.Note(
                        $"session_replay_unavailable: {replay.Error}",
                        Tui.TuiToken.Warn);
                    sessionRenderer.Flush();
                }
            }

            if (modelWarning is not null)
            {
                if (cmd.Json)
                {
                    // Nothing human may land on the NDJSON stdout.
                    await WriteDiagnosticLineAsync(_err, modelWarning).ConfigureAwait(false);
                }
                else
                {
                    sessionRenderer.Note(modelWarning, Tui.TuiToken.Warn);
                    sessionRenderer.Flush();
                }
            }

            if (pendingImages.Count > 0
                && activeModel.Length > 0
                && !Images.CliVisionModels.IsVisionModel(activeModel))
            {
                var visionWarning =
                    $"model_vision_unsupported: {activeModel} may not read images; switch to a vision model with /model";
                if (cmd.Json)
                {
                    await WriteDiagnosticLineAsync(_err, visionWarning).ConfigureAwait(false);
                }
                else
                {
                    sessionRenderer.Note(visionWarning, Tui.TuiToken.Warn);
                    sessionRenderer.Flush();
                }
            }

            await RefreshModelUsageInfoAsync(cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(cmd.SessionId))
            {
                // Only an explicit --session reopens someone else's session; a fresh session
                // has no run to adopt and must not spend the round trip.
                await AdoptSessionRunAsync(sessionId).ConfigureAwait(false);
            }

            // The non-slash branch of the sequential key loop, moved into the shape the
            // dispatcher expects. 'continue' became 'return' (both resume the key loop),
            // and dispatcherExitCode carries the paths that used to return from ChatAsync.
            sessionCommands = CreateSessionCommands();

            try
            {
                if (tgMode)
                {
                    await RunTerminalGuiChatAsync().ConfigureAwait(false);
                    if (!tgWindow!.HandOverToSequential || dispatcherExitCode is not null)
                        return dispatcherExitCode ?? 0;

                    // One-way handover after a tiny viewport stops the frame. Dispose the
                    // alternate screen before writing to stdout; session/run state stays intact.
                    tgMode = false;
                    tgWriter = null;
                    _slashCommands.ReprintPanelOnPickerCancel = false;
                    _slashCommands.FramelessReports = false;
                    humanOut = _out;
                    _slashCommands.HumanOut = _out;
                    // The Terminal.Gui delegates now point at a disposed view. Drop the
                    // picker and masked panel, and rebuild the session list paths so
                    // commands use the sequential fallbacks.
                    _slashCommands.Picker = null;
                    _slashCommands.SecretPanelPrompt = null;
                    _slashCommands.SecretPrompt = (prompt, ct) => Tui.ConsoleSecretInput.ReadAsync(_out, prompt, ct);
                    sessionCommands = CreateSessionCommands();
                    sessionRenderer = CreateSessionRenderer(_out);
                    tgApp!.Dispose();
                }

                var dispatcher = new ChatLineDispatcher(
                    HandleSlashLineAsync,
                    (prompt, ct) => HandlePromptLineAsync(prompt, ct));
                return await lineReader.RunAsync(_in, humanOut, async (line, ct) =>
                {
                    line = line.Trim();
                    if (line.Length == 0 && submissions.Count == 0) return null;
                    // Quotes belong to the prompt unless the whole line is an existing file.
                    var candidate = line.Trim('"', '\'');
                    if (candidate.Length > 0 && IsDroppedAttachmentPath(candidate))
                    {
                        await AttachFileAsync(candidate).ConfigureAwait(false);
                        return null;
                    }
                    await dispatcher.DispatchAsync(line, ct).ConfigureAwait(false);
                    return dispatcherExitCode;
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                submissions.ClearPending();
            }
        }
    }
}
