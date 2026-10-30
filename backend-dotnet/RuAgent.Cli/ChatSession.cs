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
        private CancellationToken cancellationToken;

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
        private string? initialDraftSessionId;
        private bool draftCreationFailed;
        private string workspaceLabel = default!;
        private string backendName = default!;
        private string activeModel = default!;
        private string activeReasoningEffort = string.Empty;
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

        private TuiSessionManager? multiSessions;
        private AgentSlashCommands? sessionSlashCommands;
        private RunStartCore? sessionRunStartCore;
        // The Host admits at most one active root per conversation.
        private string? ownedRunId;
        private volatile bool managedShutdown;
        private bool modelUsageRefreshRequired;
        private readonly ChatAttachmentTasks attachmentTasks = new();

        public async Task<int> RunAsync()
        {
            await InitializeAsync().ConfigureAwait(false);
            using var runSessionLease = runSession;

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
