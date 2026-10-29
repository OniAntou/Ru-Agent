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
        /// <summary>
        /// Reprints the loaded replay through the same renderer a live turn uses: the prompt
        /// echo for user rows and the streaming/markdown assistant path for replies. Nothing
        /// is invented here, so a replay row reads exactly like the row the turn produced.
        /// </summary>
        void RenderSessionReplay(
            string targetSessionId,
            ChatSessionReplayResult replay)
        {
            sessionRenderer.Note(
                $"↺ replayed the last {replay.Messages.Length} messages from session {targetSessionId}",
                Tui.TuiToken.Meta);
            foreach (var message in replay.Messages)
            {
                if (string.Equals(message.Role, "user", StringComparison.Ordinal))
                {
                    sessionRenderer.Surface.WriteLine(
                        Tui.ChatPromptEcho.Format(message.Text));
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
                        return;
                    }

                    if (!ChatRunWorkflow.IsFollowable(run))
                    {
                        continue;
                    }

                    await ShowRetainedRunProviderAsync(run).ConfigureAwait(false);
                    runSession.RememberFollowable(candidateRunId);
                    var followNotice = string.Equals(run.Status, "waiting_input", StringComparison.OrdinalIgnoreCase)
                        ? $"run {candidateRunId} is waiting for an answer · use /follow {candidateRunId}"
                        : string.Equals(run.Status, "waiting_approval", StringComparison.OrdinalIgnoreCase)
                        ? $"run {candidateRunId} is waiting for approval · use /follow {candidateRunId}"
                        : $"run {candidateRunId} is still running · attach with 'ru agent attach {candidateRunId}' or follow with '/follow {candidateRunId}'";
                    if (cmd.Json)
                    {
                        await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.DimPaint(followNotice)).ConfigureAwait(false);
                    }
                    else
                    {
                        sessionRenderer.Note(followNotice, Tui.TuiToken.Meta);
                    }
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
                // REPL start; the headless 'ru agent continue <run-id>' remains available.
                Tui.TuiDebug.Log("adopt", $"session run adoption failed: {CliErrorText.ShortMessage(ex.Message)}");
            }
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
        /// Rebuilds the session-command surface for the current engine. A tiny-terminal
        /// handover disposes the Terminal.Gui frame, so its pickers and delete card must be
        /// replaced by the sequential list paths; reusing the old delegate would call a
        /// disposed panel.
        /// </summary>
        ChatSessionCommands CreateSessionCommands() =>
            new(
                () => runSession.IsTurnActive,
                () => sessionId,
                ct => _sessionClient.CreateAsync(ct),
                async (query, ct) => ChatSessionWorkflow.Filter(
                    await _sessionClient.ListAsync(ct).ConfigureAwait(false), query),
                SwitchSessionAsync,
                _slashCommands.Picker,
                // The session card (switch + delete) and the delete call exist only where a panel
                // can be drawn; the sequential engine keeps the plain list and /sessions id:<id>.
                tgMode ? (list, ct) => TgPanelRequests.RequestSessionChoiceAsync(tgWindow!, list, ct) : null,
                tgMode ? DeleteSessionAsync : null,
                WriteDiagnosticLineAsync,
                humanOut,
                _err,
                cmd.Json);

        Task<bool> HandleSessionWorkflowAsync(string slashCmd, string? arg) =>
            sessionCommands.TryHandleAsync(slashCmd, arg, cancellationToken);

        async Task SwitchSessionAsync(string targetSessionId)
        {
            if (!ChatSessionWorkflow.IsValidSessionId(targetSessionId))
            {
                await WriteDiagnosticLineAsync(_err, "invalid_session_id: expected a bounded session identifier").ConfigureAwait(false);
                return;
            }

            var sameSession = string.Equals(targetSessionId, sessionId, StringComparison.Ordinal);
            if (sameSession && !historyReloadRequired)
            {
                await WriteDiagnosticLineAsync(humanOut, $"already in session {sessionId}").ConfigureAwait(false);
                return;
            }
            if (sameSession && historyReloadRequired)
            {
                await ReloadUncertainHistoryAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var replay = await ChatSessionReplay.LoadAsync(_sessionClient, targetSessionId, cancellationToken).ConfigureAwait(false);
            if (replay.Error is not null)
            {
                await WriteDiagnosticLineAsync(_err, replay.Error).ConfigureAwait(false);
                return;
            }

            historyReloadRequired = false;

            pendingHistoryBranch = null;
            sessionId = targetSessionId;
            sessionTitle = replay.Title;
            runSession.ResetSession();
            sessionTitleProbes = 0;
            turn = 0;
            lastTotalTokens = null;
            usage = new Tui.TuiUsageTracker();
            ClearSubagentStrip();
            await RefreshModelUsageInfoAsync(cancellationToken).ConfigureAwait(false);

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
                    tgWindow.Header.SetTitle(sessionTitle ?? "");
                    tgWindow.Composer.SetMeta(cmd.Preset, activeModel, backendName ?? "(provider unknown)");
                });
            }

            if (replay.Messages.Length > 0 && !cmd.Json)
            {
                RenderSessionReplay(targetSessionId, replay);
            }

            await AdoptSessionRunAsync(targetSessionId).ConfigureAwait(false);

            await RefreshModelSelectionAsync().ConfigureAwait(false);
            var otherWorkspace = await ChatSessionWorkflow.ReadWorkspaceLabelAsync(
                _sessionClient,
                targetSessionId,
                cancellationToken).ConfigureAwait(false);
            if (otherWorkspace is { } recordedWorkspace
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

            await WriteDiagnosticLineAsync(humanOut, $"switched to session {targetSessionId}").ConfigureAwait(false);
        }
    }
}
