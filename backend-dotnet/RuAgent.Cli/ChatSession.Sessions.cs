using System.Collections.Immutable;
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
    private sealed partial class ChatSession
    {
        /// <summary>Writes one prompt read back from history; the choice lives in <see cref="SessionHistoryPromptWriter"/>.</summary>
        void WriteReplayPrompt(string prompt) =>
            SessionHistoryPromptWriter.Write(
                prompt,
                tgMode,
                typed => chatEngine?.EchoPrompt(typed),
                line => sessionRenderer.Surface.WriteLine(line));

        /// <summary>
        /// Redraws a reopened session's newest turns the way they looked live: prompt,
        /// narration, thoughts, tool rows and the footer. Falls back to the text replay
        /// when the turn record cannot be read.
        /// </summary>
        async Task RenderSessionHistoryAsync(
            string targetSessionId,
            ChatSessionReplayResult fallback,
            CancellationToken cancellationToken)
        {
            var turns = await ChatSessionTurnReplay.LoadAsync(_sessionClient, _agentClient, targetSessionId, cancellationToken)
                .ConfigureAwait(false);
            if (turns is not { Length: > 0 } loaded)
            {
                RenderSessionReplay(targetSessionId, fallback);
                return;
            }

            sessionRenderer.Note(
                $"↺ replayed the last {loaded.Length} {(loaded.Length == 1 ? "turn" : "turns")} from session {targetSessionId}",
                Tui.TuiToken.Meta);
            RenderSessionTurns(loaded);
        }

        private void RenderSessionTurns(ImmutableArray<ChatReplayTurn> loaded,
            TuiSessionRenderer? renderer = null, Action<string>? writePrompt = null)
        {
            renderer ??= sessionRenderer;
            writePrompt ??= WriteReplayPrompt;
            foreach (var turn in loaded)
            {
                foreach (var prompt in turn.Prompts)
                {
                    writePrompt(prompt);
                }
                if (turn.Run is { TerminalAt: not null } run)
                {
                    // A long run replays its newest steps; the rest load on request.
                    if (turn.Activity is { HasMore: true } activity)
                        renderer.Note(ChatSessionTurnReplay.OlderStepsNotice(activity.OlderCount, loadsOnScroll: tgMode && !cmd.Json),
                            Tui.TuiToken.Meta);
                    renderer.BeginTurn();
                    renderer.Transcript.ReplayTurn(turn.Activity, turn.Replies,
                        ChatSessionTurnReplay.Time(run.CreatedAt), ChatSessionTurnReplay.Time(run.TerminalAt),
                        ChatSessionTurnReplay.Outcome(run.Status), run.TerminalErrorCode);
                }
                else if (turn.Run is null)
                {
                    // No record (or an unreadable one): the reply text is all there is.
                    foreach (var reply in turn.Replies)
                    {
                        renderer.Transcript.AppendAssistant(reply);
                        renderer.Flush();
                        renderer.Surface.Line();
                    }
                }
                // A run still working is drawn by the follow that adopts it next.
            }

            renderer.Flush();
        }

        /// <summary>
        /// Reprints the loaded replay through the same renderer a live turn uses: the prompt
        /// echo for user rows and the streaming/markdown assistant path for replies. Nothing
        /// is invented here, so a replay row reads exactly like the row the turn produced.
        /// </summary>
        void RenderSessionReplay(
            string targetSessionId,
            ChatSessionReplayResult replay)
        {
            if (!tgMode)
                sessionRenderer.Note(
                    $"↺ replayed the last {replay.Messages.Length} messages from session {targetSessionId}",
                    Tui.TuiToken.Meta);
            foreach (var message in replay.Messages)
            {
                if (string.Equals(message.Role, "user", StringComparison.Ordinal))
                {
                    WriteReplayPrompt(message.Text);
                }
                else
                {
                    sessionRenderer.Transcript.AppendAssistant(message.Text);
                    sessionRenderer.Flush();
                    // The streaming path leaves the reply row open for the next delta;
                    // a replay message is final, so the row must end before the next one.
                    sessionRenderer.Surface.Line();
                }
            }

            if (replay.Shortened > 0 || replay.Hidden > 0)
            {
                var parts = new List<string>();
                if (replay.Shortened > 0)
                {
                    parts.Add($"{replay.Shortened} shortened");
                }

                if (replay.Hidden > 0)
                {
                    parts.Add($"{replay.Hidden} tool/other rows hidden");
                }

                sessionRenderer.Note("… " + string.Join(", ", parts), Tui.TuiToken.Meta);
            }

            sessionRenderer.Flush();
        }

        /// <summary>
        /// Takes over the newest active run when the session was reopened with
        /// --session. After a host restart a parked or approval-waiting run is invisible otherwise: the CLI
        /// claims no parked run, so bare Continue would send an ordinary prompt and the
        /// run would sit parked forever. The newest run only is adopted, matching the
        /// GUI. Fail-soft: a read fault prints nothing and never breaks the REPL start.
        /// </summary>
        async Task<GetAgentRunResponse?> AdoptSessionRunAsync(string targetSessionId,
            IReadOnlyList<GetAgentRunResponse>? knownRuns = null)
        {
            try
            {
                var runs = knownRuns ?? await _agentClient
                    .ListSessionRunsAsync(targetSessionId, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var summary in runs.TakeLast(100).Reverse())
                {
                    if (summary.TerminalAt is not null || IsTerminalRunStatus(summary.Status))
                    {
                        continue;
                    }

                    // Only a potentially live run needs fresh authority state. Terminal
                    // list entries already contain everything required to skip them.
                    var candidateRunId = summary.RunId;
                    var run = await _agentClient.GetRunAsync(candidateRunId, cancellationToken).ConfigureAwait(false);
                    if (run.SessionId != targetSessionId || run.TerminalAt is not null || IsTerminalRunStatus(run.Status)) continue;

                    if (IsParkedRunStatus(run.Status))
                    {
                        await ShowRetainedRunProviderAsync(run, deferUsage: tgMode).ConfigureAwait(false);
                        runSession.RememberParked(candidateRunId);
                        var notice = RetainedRunNotice(run);
                        if (cmd.Json)
                        {
                            await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Warn(notice)).ConfigureAwait(false);
                        }
                        else
                        {
                            sessionRenderer.Note(notice, Tui.TuiToken.Warn);
                        }
                        return run;
                    }

                    if (!ChatRunWorkflow.IsFollowable(run))
                    {
                        continue;
                    }

                    await ShowRetainedRunProviderAsync(run, deferUsage: tgMode).ConfigureAwait(false);
                    runSession.RememberFollowable(candidateRunId);
                    var followNotice = RetainedRunNotice(run);
                    if (cmd.Json)
                    {
                        await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.DimPaint(followNotice)).ConfigureAwait(false);
                    }
                    else
                    {
                        sessionRenderer.Note(followNotice, Tui.TuiToken.Meta);
                    }
                    return run;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Adoption is best effort: an unreachable list/read must not break the
                // REPL start; the headless 'ru agent attach <run-id>' remains available.
                Tui.TuiDebug.Log("adopt", $"session run adoption failed: {CliErrorText.ShortMessage(ex.Message)}");
            }
            return null;
        }

        async Task<SessionDeleteResult> DeleteSessionAsync(string targetId, CancellationToken ct)
        {
            try
            {
                await _sessionClient.DeleteAsync(targetId, ct).ConfigureAwait(false);
                return new SessionDeleteResult(Deleted: true, Message: null);
            }
            catch (AgentApiException ex) when (string.Equals(
                ex.Message,
                SessionDeleteResult.PartialArtifactsMessage,
                StringComparison.Ordinal))
            {
                // The chat is gone; only its artifacts remain. The host's own advice is to
                // delete again, so this reads as a delete with a retry hint, not a failure.
                return new SessionDeleteResult(Deleted: true, Message: SessionDeleteResult.PartialArtifactsMessage);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new SessionDeleteResult(Deleted: false, Message: CliErrorText.ShortMessage(ex.Message));
            }
        }

        /// <summary>
        /// The last session was deleted: return to the empty landing instead of creating a
        /// replacement session. A fresh chat creates its session lazily on the first prompt,
        /// so deleting the last conversation no longer leaves an empty "Cuộc trò chuyện mới"
        /// row behind (which the same delete flow would otherwise create again on every pass).
        /// </summary>
        async Task LeaveDeletedSessionAsync(CancellationToken ct)
        {
            CancelSessionEnrichment();
            sessionId = string.Empty;
            cmd = cmd with { SessionId = null };
            sessionTitle = null;
            runSession.ResetSession();
            ownedRunId = null;
            turn = 0;
            sessionTitleProbes = 0;
            lastTotalTokens = null;
            usage = new Tui.TuiUsageTracker();
            preparedContext = null;
            initialDraftSessionId = null;
            draftCreationFailed = false;
            if (tgMode) chatEngine?.ResetInputHistory();
            ClearSubagentStrip();
            submissions.ClearPending();
            RefreshAttachmentLabels();
            sessionRenderer = CreateSessionRenderer(ResolveRenderWriter());
            sessionRenderer.Width = ChatRenderWidth();
            if (tgMode)
            {
                RunOnTgUi(() =>
                {
                    tgWindow!.ResetTranscript();
                    tgWindow.Hints.ClearWorkspaceBusy();
                    tgWindow.Hints.SetIdle(workspaceLabel);
                    tgWindow.Header.SetTitle(string.Empty);
                    tgWindow.Header.UpdateUsage(0, null, null);
                    tgWindow.Composer.SetMeta(cmd.Preset, activeModel, backendName ?? "(provider unknown)");
                    tgWindow.Composer.SetReasoningEffort(activeReasoningEffort);
                });
            }

            await RefreshModelUsageInfoAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Binds session commands to the selected presentation's picker capabilities.
        /// </summary>
        ChatSessionCommands CreateSessionCommands() =>
            new(
                () => runSession.IsTurnActive || tgMode && (runSession.ParkedRunId is not null
                    || runSession.FollowableRunId is not null || attachmentTasks.HasPending),
                () => sessionId,
                ct => _sessionClient.CreateAsync(ct),
                async (query, ct) => ChatSessionWorkflow.Filter(
                    await _sessionClient.ListAsync(ct).ConfigureAwait(false), query),
                SwitchSessionAsync,
                _slashCommands.Picker,
                // The session card (switch + delete) and the delete call exist only where a panel
                // can be drawn; line I/O keeps the plain list and /sessions id:<id>.
                tgMode ? (list, ct) => TgPanelRequests.RequestSessionChoiceAsync(tgWindow!, list, ct) : null,
                tgMode ? DeleteSessionAsync : null,
                tgMode ? LeaveDeletedSessionAsync : null,
                WriteDiagnosticLineAsync,
                humanOut,
                _err,
                cmd.Json);

        async Task<bool> HandleSessionWorkflowAsync(string slashCmd, string? arg)
        {
            if (slashCmd is not ("/new" or "/sessions")) return false;
            var handled = false;
            var token = cancellationToken;
            await RunCommandControlAsync(slashCmd.TrimStart('/'), async () =>
                handled = await sessionCommands.TryHandleAsync(slashCmd, arg, token).ConfigureAwait(false), token, captureOnChat: true).ConfigureAwait(false);
            return handled;
        }

        async Task SwitchSessionAsync(string targetSessionId)
        {
            if (!ChatSessionWorkflow.IsValidSessionId(targetSessionId))
            {
                await WriteDiagnosticLineAsync(_err, "invalid_session_id: expected a bounded session identifier").ConfigureAwait(false);
                return;
            }

            var sameSession = string.Equals(targetSessionId, sessionId, StringComparison.Ordinal);
            if (sameSession)
            {
                await ReportSessionNavigationAsync($"already in session \"{sessionTitle ?? "Untitled session"}\"").ConfigureAwait(false);
                return;
            }

            var replay = await ChatSessionReplay.LoadAsync(_sessionClient, targetSessionId, cancellationToken).ConfigureAwait(false);
            if (replay.Error is not null)
            {
                await WriteDiagnosticLineAsync(_err, replay.Error).ConfigureAwait(false);
                return;
            }

            var openingGeneration = CancelSessionEnrichment();

            // Resolve the target's workspace and permission before replacing any
            // current state. A failed read must not inherit this session's grants.
            var nextWorkspace = (Path: canonicalWorkspace, Label: workspaceLabel);
            var nextPreset = cmd.Preset;
            if (tgMode)
            {
                var recorded = await ChatSessionWorkflow.ReadWorkspaceLabelAsync(
                    _sessionClient, targetSessionId, cancellationToken).ConfigureAwait(false);
                nextWorkspace = ChatSessionWorkflow.ResolveOpenWorkspace(canonicalWorkspace, workspaceLabel, recorded);
                nextPreset = cmd.Preset == "plan" ? "plan" : "standard";
                try
                {
                    var stored = await _agentClient.GetSessionPresetAsync(targetSessionId, cancellationToken).ConfigureAwait(false);
                    if (stored is { } selection) nextPreset = selection.Preset;
                }
                catch (AgentApiException error) when (error.StatusCode == 404) { }
            }

            sessionId = targetSessionId;
            canonicalWorkspace = nextWorkspace.Path;
            workspaceLabel = nextWorkspace.Label;
            cmd = cmd with { SessionId = targetSessionId, WorkspacePath = canonicalWorkspace,
                Preset = nextPreset, ConfirmFullAccess = nextPreset == "full_access" };
            sessionTitle = replay.Title;
            runSession.ResetSession();
            sessionTitleProbes = 0;
            turn = 0;
            lastTotalTokens = null;
            usage = new Tui.TuiUsageTracker();
            preparedContext = null;
            ownedRunId = null;
            initialDraftSessionId = null;
            draftCreationFailed = false;
            if (tgMode) chatEngine?.ResetInputHistory();
            ClearSubagentStrip();

            submissions.ClearPending();
            RefreshAttachmentLabels();

            sessionRenderer = CreateSessionRenderer(ResolveRenderWriter());
            sessionRenderer.Width = ChatRenderWidth();
            if (tgMode)
            {
                RunOnTgUi(() =>
                {
                    tgWindow!.ResetTranscript();
                    tgWindow.Hints.ClearWorkspaceBusy();
                    tgWindow.Hints.SetIdle(workspaceLabel);
                    tgWindow.Header.SetTitle(sessionTitle ?? "");
                    tgWindow.Header.UpdateUsage(0, null, null);
                    tgWindow.Composer.SetMeta(cmd.Preset, activeModel, backendName ?? "(provider unknown)");
                });
            }

            if (tgMode)
            {
                if (!cmd.Json) RenderSessionReplay(targetSessionId, replay);
                await RefreshModelSelectionAsync(refreshUsage: false).ConfigureAwait(false);
                var runs = await ReadSessionSummariesAsync(targetSessionId).ConfigureAwait(false);
                var retained = await AdoptSessionRunAsync(targetSessionId, runs).ConfigureAwait(false);
                StartSessionEnrichment(targetSessionId, replay, runs, retained, openingGeneration);
            }
            else
            {
                if (replay.Messages.Length > 0 && !cmd.Json)
                    await RenderSessionHistoryAsync(targetSessionId, replay, cancellationToken).ConfigureAwait(false);
                await AdoptSessionRunAsync(targetSessionId).ConfigureAwait(false);
                await RefreshModelSelectionAsync().ConfigureAwait(false);
            }
            // The native switch already resolved and adopted the recorded workspace.
            // Line mode keeps its launch workspace and reports any difference.
            if (!tgMode && await ChatSessionWorkflow.ReadWorkspaceLabelAsync(
                    _sessionClient, targetSessionId, cancellationToken).ConfigureAwait(false) is { } recordedWorkspace
                && !string.Equals(recordedWorkspace.Path, canonicalWorkspace, StringComparison.OrdinalIgnoreCase))
            {
                var currentWorkspaceLabel = ChatDisplayText.Sanitize(
                    Tui.TuiHeader.ShortenWorkspace(canonicalWorkspace, 64), 64);
                var workspaceArgument = ChatDisplayText.Sanitize(recordedWorkspace.Path, 512);
                if (string.Equals(workspaceArgument, recordedWorkspace.Path, StringComparison.Ordinal))
                {
                    await WriteDiagnosticLineAsync(
                        humanOut,
                        $"session_workspace_diff: session was last used in {recordedWorkspace.Label}; this chat remains in {currentWorkspaceLabel}. Open 'ru agent chat --workspace \"{workspaceArgument}\" --session {targetSessionId}' to use its recorded workspace.").ConfigureAwait(false);
                }
            }

            await ReportSessionNavigationAsync($"switch to session \"{sessionTitle ?? "Untitled session"}\"").ConfigureAwait(false);
        }

        private Task ReportSessionNavigationAsync(string notice)
        {
            if (!tgMode) return WriteDiagnosticLineAsync(humanOut, notice);
            // Keep navigation feedback outside the restored transcript.
            RunOnTgUi(() => tgWindow!.Hints.ShowCommandFeedback(notice, false));
            return Task.CompletedTask;
        }
    }
}
