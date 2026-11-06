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
            if (tgMode && !cmd.Json)
            {
                nativeCommandOutput = new TgCommandOutput(tgWindow!, tgWriter!);
                humanOut = nativeCommandOutput.Output;
            }
            submissions = bootstrap.Submissions;
            canonicalWorkspace = bootstrap.CanonicalWorkspace;
            sessionId = bootstrap.SessionId;
            workspaceLabel = bootstrap.WorkspaceLabel;
            backendName = bootstrap.BackendName;
            activeModel = bootstrap.ActiveModel;
            activeReasoningEffort = bootstrap.ReasoningEffort;
            modelWarning = bootstrap.ModelWarning;
            // The shared controller is configured once the run handlers exist;
            // attachment mutations below refresh its hint row through this seam.
            sessionRenderer = CreateSessionRenderer(ResolveRenderWriter());

            // Header state for the Terminal.Gui header. The title is read from the
            // session document (bounded probes) and the tally repaints as usage frames arrive.
            usage = new Tui.TuiUsageTracker();
            subagentTracker = new Subagents.SubagentLiveTracker();
            modelUsage = new ChatModelUsage((backend, model, ct) => _settingsClient.GetModelDetailsAsync(backend, model, ct));
            runSession = new ChatRunSession();

            ConfigureSlashCommands();
            await RefreshModelSelectionAsync().ConfigureAwait(false);

            if (!cmd.Json && !string.IsNullOrWhiteSpace(cmd.SessionId))
            {
                // A reopened session shows its last messages; the TG frame owns its welcome view.
                var replay = await ChatSessionReplay.LoadAsync(_sessionClient, sessionId, cancellationToken).ConfigureAwait(false);
                if (replay.Title is not null)
                {
                    sessionTitle = replay.Title;
                    // The title probe skips a session whose title is already known, so the
                    // header kept its "session" caption on every reopen without this.
                    if (tgMode) RunOnTgUi(() => tgWindow!.Header.SetTitle(sessionTitle));
                }
                if (replay.Messages.Length > 0)
                {
                    await RenderSessionHistoryAsync(sessionId, replay, cancellationToken).ConfigureAwait(false);
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

            if (submissions.ImageCount > 0
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

        private void ConfigureSlashCommands()
        {
            // Slash-command surfaces use native panels or explicit line arguments.
            // --json owns stdout, so every human-facing line a slash command prints goes
            // to stderr and picker-only commands refuse instead of prompting.
            _slashCommands.HumanOut = humanOut;
            _slashCommands.Json = cmd.Json;
            _slashCommands.PanelWidth = Tui.TuiViewport.Width();
            _slashCommands.FramelessReports = tgMode;
            // Only the native presentation has a floating picker. The line adapter
            // leaves Picker null so the command handlers print their option
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
        }
    }
}
