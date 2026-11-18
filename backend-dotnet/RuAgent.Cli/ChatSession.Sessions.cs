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
            if (tgMode) RenderSessionReplay(targetSessionId, fallback);

            var turns = await ChatSessionTurnReplay.LoadAsync(_sessionClient, _agentClient, targetSessionId, cancellationToken)
                .ConfigureAwait(false);
            if (turns is not { Length: > 0 } loaded)
            {
                if (!tgMode) RenderSessionReplay(targetSessionId, fallback);
                return;
            }

            if (!tgMode)
            {
                sessionRenderer.Note(
                    $"↺ replayed the last {loaded.Length} {(loaded.Length == 1 ? "turn" : "turns")} from session {targetSessionId}",
                    Tui.TuiToken.Meta);
                RenderSessionTurns(loaded);
                return;
            }

            // The switch still owns the submission lane, so the only rows to replace
            // are this session's preview. Keep the reader's distance from the tail.
            var transcript = tgWindow!.Transcript;
            var rowsBelow = 0;
            RunOnTgUi(() =>
            {
                rowsBelow = transcript.RowsBelowViewport;
                transcript.Clear();
                transcript.DeferWrapping = true;
            });
            try
            {
                RenderSessionTurns(loaded);
            }
            finally
            {
                RunOnTgUi(() =>
                {
                    transcript.DeferWrapping = false;
                    if (rowsBelow > 0) transcript.ScrollRows(-rowsBelow);
                });
            }
        }

        private void RenderSessionTurns(ImmutableArray<ChatReplayTurn> loaded)
        {
            foreach (var turn in loaded)
            {
                foreach (var prompt in turn.Prompts)
                {
                    WriteReplayPrompt(prompt);
                }
                if (turn.Run is { TerminalAt: not null } run)
                {
                    sessionRenderer.BeginTurn();
                    sessionRenderer.Transcript.ReplayTurn(turn.Activity, turn.Replies,
                        ChatSessionTurnReplay.Time(run.CreatedAt), ChatSessionTurnReplay.Time(run.TerminalAt),
                        ChatSessionTurnReplay.Outcome(run.Status), run.TerminalErrorCode);
                }
                else if (turn.Run is null)
                {
                    // No record (or an unreadable one): the reply text is all there is.
                    foreach (var reply in turn.Replies)
                    {
                        sessionRenderer.Transcript.AppendAssistant(reply);
                        sessionRenderer.Flush();
                        sessionRenderer.Surface.Line();
                    }
                }
                // A run still working is drawn by the follow that adopts it next.
            }

            sessionRenderer.Flush();
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
        async Task AdoptSessionRunAsync(string targetSessionId)
        {
            try
            {
                var runIds = await _agentClient
                    .ListSessionRunIdsAsync(targetSessionId, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var candidateRunId in runIds.TakeLast(100).Reverse())
                {
                    var run = await _agentClient.GetRunAsync(candidateRunId, cancellationToken).ConfigureAwait(false);
                    if (run.TerminalAt is not null || IsTerminalRunStatus(run.Status))
                    {
                        continue;
                    }

                    if (IsParkedRunStatus(run.Status))
                    {
                        await ShowRetainedRunProviderAsync(run).ConfigureAwait(false);
                        runSession.RememberParked(candidateRunId);
                        var notice = Tui.TuiTranscript.FormatParkedNotice(candidateRunId);
                        if (cmd.Json)
                        {
                            await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Warn(notice)).ConfigureAwait(false);
                        }
                        else
                        {
                            sessionRenderer.Note(notice, Tui.TuiToken.Warn);
                        }
                        await RestoreRetainedTodosAsync(candidateRunId).ConfigureAwait(false);
                        return;
                    }

                    if (!ChatRunWorkflow.IsFollowable(run))
                    {
                        continue;
                    }

                    await ShowRetainedRunProviderAsync(run).ConfigureAwait(false);
                    runSession.RememberFollowable(candidateRunId);
                    var followNotice = string.Equals(run.Status, "waiting_input", StringComparison.OrdinalIgnoreCase)
                        ? $"run {candidateRunId} is waiting for an answer · type Continue"
                        : string.Equals(run.Status, "waiting_approval", StringComparison.OrdinalIgnoreCase)
                        ? $"run {candidateRunId} is waiting for approval · type Continue"
                        : $"run {candidateRunId} is still running · attach with 'ru agent attach {candidateRunId}' or type Continue here";
                    if (cmd.Json)
                    {
                        await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.DimPaint(followNotice)).ConfigureAwait(false);
                    }
                    else
                    {
                        sessionRenderer.Note(followNotice, Tui.TuiToken.Meta);
                    }
                    await RestoreRetainedTodosAsync(candidateRunId).ConfigureAwait(false);
                    return;
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
        }

        async Task RestoreRetainedTodosAsync(string runId)
        {
            if (!tgMode || cmd.Json) return;
            var replay = await _agentClient.ListRunActivityReplayAsync(runId, cancellationToken).ConfigureAwait(false);
            sessionRenderer.Transcript.RestoreTodos(replay);
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

            // History and the restored meter load together, so saved messages appear without
            // waiting on the usage replay. The session payload carries no usage, so the meter
            // comes from the target's newest run rather than staying empty until its next turn.
            var history = replay.Messages.Length > 0 && !cmd.Json
                ? RenderSessionHistoryAsync(targetSessionId, replay, cancellationToken)
                : Task.CompletedTask;
            await Task.WhenAll(history, RestoreSwitchedSessionUsageAsync(targetSessionId, cancellationToken))
                .ConfigureAwait(false);
            // The restored reading is the last request; the reply it produced came after it.
            if (replay.Messages.LastOrDefault() is { } lastMessage && !string.Equals(lastMessage.Role, "user", StringComparison.Ordinal))
            {
                usage.ObserveReplyTail(lastMessage.Text.Length);
                PaintContextUsage();
            }

            await AdoptSessionRunAsync(targetSessionId).ConfigureAwait(false);

            await RefreshModelSelectionAsync().ConfigureAwait(false);
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

        private async Task RestoreSwitchedSessionUsageAsync(string targetSessionId, CancellationToken ct)
        {
            await RefreshModelUsageInfoAsync(ct).ConfigureAwait(false);
            await RestoreSessionUsageAsync(targetSessionId, ct).ConfigureAwait(false);
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
