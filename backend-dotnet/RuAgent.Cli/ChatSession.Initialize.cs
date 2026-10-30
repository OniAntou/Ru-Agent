using System.Text.Json;
using RuAgent.Cli.Subagents;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        private async Task InitializeAsync()
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
            activeReasoningEffort = bootstrap.ReasoningEffort;
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
                SessionId: string.IsNullOrEmpty(sessionId) ? "not started" : sessionId,
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

        }
    }
}
