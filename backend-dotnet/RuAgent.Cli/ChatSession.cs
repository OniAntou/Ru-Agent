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
        private Chat.ChatSessionEngine? chatEngine;
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
                    return dispatcherExitCode ?? 0;
                }

                var view = new Chat.PlainChatView(_err, workspaceLabel);
                var engine = new Chat.ChatSessionEngine(view, cancellationToken);
                ConfigureChatEngine(engine);
                await Chat.ChatLineAdapter.RunAsync(_in, humanOut, view, engine,
                    attachmentTasks.WaitForIdleAsync, cancellationToken).ConfigureAwait(false);
                return dispatcherExitCode ?? 0;
            }
            finally
            {
                submissions.ClearPending();
            }
        }
    }
}
