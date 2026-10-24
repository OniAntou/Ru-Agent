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
        async Task HandlePromptLineAsync(
            string promptLine,
            CancellationToken ct,
            ChatSubmissionSnapshot? submission = null)
        {
            var submissionSessionId = submission?.SessionId ?? sessionId;
            var submissionWorkspace = submission?.WorkspacePath ?? canonicalWorkspace;
            var submissionPreset = submission?.Preset ?? cmd.Preset;
            IReadOnlyList<Images.CliImageAttachment> submissionImages = submission is null
                ? pendingImages
                : submission.Images;
            IReadOnlyList<Attachments.CliTextAttachment> submissionTexts = submission is null
                ? pendingTexts
                : submission.Texts;
            if (submission is not null
                && !submission.MatchesContext(sessionId, canonicalWorkspace, cmd.Preset, backendName ?? string.Empty, activeModel))
            {
                HoldSubmissionForRetry(submission);
                await WriteDiagnosticLineAsync(_err, "submission_context_changed: queued prompt is held; restore its session/model selection and use /retry").ConfigureAwait(false);
                return;
            }

            if (submission is not null)
            {
                try
                {
                    var liveSettings = await _settingsClient.GetAsync(ct).ConfigureAwait(false);
                    if (!submission.MatchesSelection(liveSettings))
                    {
                        HoldSubmissionForRetry(submission);
                        await WriteDiagnosticLineAsync(_err, "submission_context_changed: backend/model settings changed; restore the admitted selection and use /retry").ConfigureAwait(false);
                        return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    HoldSubmissionForRetry(submission);
                    await WriteDiagnosticLineAsync(_err, "submission_settings_unavailable: queued prompt is held; restore settings access and use /retry").ConfigureAwait(false);
                    return;
                }
            }

            // The GUI accepts a bare typed "Continue" while a run is parked. Only claim
            // that literal when a parked run is known: otherwise it is an ordinary prompt.
            if (runSession.ParkedRunId is not null
                && string.Equals(promptLine, "continue", StringComparison.OrdinalIgnoreCase))
            {
                if (!cmd.Json && !tgMode)
                {
                    await _out.WriteLineAsync(Tui.ChatPromptEcho.Format(promptLine)).ConfigureAwait(false);
                }
                try
                {
                    await ContinueParkedRunAsync().ConfigureAwait(false);
                }
                catch (RunParkedException)
                {
                    // Only the --json escape ends the chat: a scripted consumer answers the
                    // parked run out of band. A terminal keeps its REPL; the run is parked
                    // server-side and the notice already names `ru agent answer`.
                    if (cmd.Json) dispatcherExitCode = 0;
                    return;
                }
                return;
            }

            // A new turn uses the current selection, not the provider pinned by a run that
            // was followed or continued earlier. Reset the composer meta before the run
            // starts; a Continue returned above and keeps the retained run's identity.
            if (tgMode)
            {
                RunOnTgUi(() => tgWindow!.Composer.SetMeta(submissionPreset, activeModel, backendName ?? "(provider unknown)"));
            }

            // The usage envelope's input/output counters restart with every run, so the
            // step-delta baseline resets here; the session cost itself keeps accumulating.
            usage.BeginTurn();
            ClearSubagentStrip();
            turn++;
            var presetUpdate = await _runStartCore.UpdateSessionPresetAsync(
                submissionSessionId,
                submissionPreset,
                cmd.ConfirmFullAccess,
                ct).ConfigureAwait(false);
            if (presetUpdate is null)
            {
                // A retryable preset conflict is not a reason to end the operator's
                // session: the submission is held for /retry and the composer stays live.
                // The fail-closed restore paths below still exit when state is unsafe.
                HoldSubmissionForRetry(submission);
                return;
            }

            var idempotencyKey = submission?.IdempotencyKey ?? Guid.NewGuid().ToString("N");
            var promptText = Attachments.AttachedPrompt.Compose(promptLine, submissionTexts);
            var imagePayloads = submissionImages.Count == 0
                ? null
                : submissionImages.Select(image => image.ToDataUrl()).ToList();
            var attachmentLabels = new List<string>();
            for (var i = 0; i < submissionImages.Count; i++)
            {
                attachmentLabels.Add($"[image {i + 1}: {submissionImages[i].FileName}]");
            }

            for (var i = 0; i < submissionTexts.Count; i++)
            {
                attachmentLabels.Add($"[file {i + 1}: {submissionTexts[i].FileName}]");
            }

            var request = new CreateAgentRunRequest(
                submissionSessionId,
                submissionWorkspace,
                promptText,
                submissionPreset,
                presetUpdate.Version,
                idempotencyKey,
                imagePayloads);
            if (promptText.Length > ChatSubmissionState.MaxComposedPromptChars)
            {
                HoldSubmissionForRetry(submission);
                await WriteDiagnosticLineAsync(
                    _err,
                    $"prompt_too_large: the composed prompt is {promptText.Length} chars (limit {ChatSubmissionState.MaxComposedPromptChars}); detach or shorten attachments; prompt held for /retry").ConfigureAwait(false);
                return;
            }
            var presetCommitted = false;
            var hostAccepted = false;
            ChatSubmissionState.Transfer? transfer = null;
            IDisposable? runSettlement = null;

            try
            {
                runSession.BeginNewTurn(ct);
                runSettlement = runSession.RetainRunSettlement();
                // Parked state is per-turn: a new prompt starts clean, so the T1 cancel
                // guard can never POST a cancel against a park left over from a prior turn.
                sessionRenderer.Width = ChatRenderWidth();
                sessionRenderer.Status.SetCounters(lastTotalTokens, 0);
                if (!cmd.Json && !tgMode)
                {
                    sessionRenderer.Surface.WriteLine(Tui.ChatPromptEcho.Format(
                        promptLine,
                        attachmentLabels: attachmentLabels.Count == 0 ? null : attachmentLabels));
                }
                sessionRenderer.BeginTurn();
                var resp = await _agentClient.CreateRunAsync(request, runSession.Token).ConfigureAwait(false);
                hostAccepted = true;
                if (tgMode) RunOnTgUi(() => tgWindow!.Hints.ClearWorkspaceBusy());
                submissions.Accept();
                RefreshAttachmentLabels();
                tgLoop?.ResumeQueuedSubmissions();
                runSession.AcceptCreatedRun(resp.RunId);

                if (submissionImages.Count > 0 || submissionTexts.Count > 0)
                {
                    // The run owns the attachments now. The lists clear immediately, but
                    // the temp files wait for the trust gate: a declined challenge restores
                    // the lists intact, so the files must still exist then.
                    transfer = submissions.BeginTransfer(submissionImages, submissionTexts, submission is null);
                    RefreshAttachmentLabels();
                }

                var isTrustWait = string.Equals(
                    resp.Status,
                    "waiting_workspace_trust",
                    StringComparison.OrdinalIgnoreCase);

                // The composer can ask, so ask here rather than parking the run. The old
                // path wrote the instruction into the transcript and left the turn live,
                // where the status row kept spinning "Thinking" and the sticky footer
                // repainted the line away: the operator saw a slow model and a frozen
                // prompt. Nothing is executing while the challenge is open, so answering it
                // in place costs nothing and keeps trust an explicit human decision.
                // --json never reaches the in-composer ask: it requires --trust-workspace,
                // so the human notice stays on stderr through RunStartCore.HandleWorkspaceTrustAsync.
                var trustDeclined = false;
                var trusted = isTrustWait && !cmd.TrustWorkspace
                    ? await ResolveTrust().ConfigureAwait(false)
                    : await _runStartCore.HandleWorkspaceTrustAsync(
                            resp,
                            submissionSessionId,
                            submissionPreset,
                            cmd.TrustWorkspace,
                            cmd.Json,
                            runSession.Token)
                        .ConfigureAwait(false);

                async Task<bool> ResolveTrust()
                {
                    var outcome = await ResolveWorkspaceTrustInPlaceAsync(
                            resp,
                            submissionSessionId,
                            submissionPreset,
                            submissionWorkspace,
                            cmd.Json,
                            runSession.Token)
                        .ConfigureAwait(false);
                    trustDeclined = outcome.Declined;
                    return outcome.Trusted;
                }

                if (!trusted && isTrustWait)
                {
                    // Either the operator declined — the run is cancelled — or there was no
                    // composer to ask in and the bounded code was already reported. Both end
                    // this turn: nothing is executing, and the live row must not keep spinning
                    // over a run that is not going to produce a reply. The attachments were
                    // never processed, so they come back for a retry.
                    if (transfer is not null)
                    {
                        transfer.ReturnToPending();
                        transfer = null;
                        RefreshAttachmentLabels();
                    }

                    await FinishTurnAsync().ConfigureAwait(false);
                    if (trustDeclined)
                    {
                        sessionRenderer.Note("workspace not trusted · this run was cancelled", Tui.TuiToken.Err);
                    }

                    return;
                }

                if (!trusted)
                {
                    if (!await _runStartCore.RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
                    {
                        await FinishTurnAsync().ConfigureAwait(false);
                        dispatcherExitCode = 1;
                        return;
                    }

                    await FinishTurnAsync().ConfigureAwait(false);
                    // The create response was not a usable run (invalid binding or an
                    // unsafe status). The preset is restored, but this turn cannot
                    // continue: report a non-zero exit instead of asking for another
                    // prompt as if the turn had succeeded.
                    dispatcherExitCode = 1;
                    return;
                }

                presetCommitted = true;
                transfer?.Dispose();
                transfer = null;

                await FollowRunAsync(resp.RunId, 0, runSession.Token).ConfigureAwait(false);
                await FinishTurnAsync().ConfigureAwait(false);
                runSession.ForgetCompletedRun();
            }
            catch (RunParkedException)
            {
                // --json approval/input escape: the bounded notice already went to stderr,
                // and the run is deliberately left waiting server-side. Ending the chat
                // hands control back to the scripted consumer, which answers it with
                // `ru agent approve` / `ru agent answer`. A terminal keeps its REPL.
                await FinishTurnAsync().ConfigureAwait(false);
                runSession.RememberFollowable(runSession.ActiveRunId);
                if (cmd.Json) dispatcherExitCode = 0;
                return;
            }
            catch (OperationCanceledException)
            {
                if (!hostAccepted)
                {
                    HoldSubmissionForRetry(submission);
                }
                else if (submission is not null)
                {
                    foreach (var image in submission.Images)
                    {
                        DeleteTempFile(image);
                    }
                }

                // The token only stops local streaming; the run must be cancelled server-side
                // too, like one-shot `agent run` and the GUI Stop, or it keeps the workspace
                // lock -- unless the run parked itself, which makes it the user's to resume.
                if (!presetCommitted
                    && !await _runStartCore.RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
                {
                    await FinishTurnAsync().ConfigureAwait(false);
                    dispatcherExitCode = 1;
                    return;
                }
                await ReportInterruptionAsync().ConfigureAwait(false);
            }
            catch (AgentApiException ex) when (ex.ErrorCode == "workspace_busy")
            {
                if (!hostAccepted)
                {
                    // A queued submission was already claimed from the pending attachment
                    // list; hold it (and release its temp files) like every other
                    // pre-accept failure so /retry can resubmit it intact.
                    HoldSubmissionForRetry(submission);
                }

                if (!presetCommitted
                    && !await _runStartCore.RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
                {
                    await FinishTurnAsync().ConfigureAwait(false);
                    dispatcherExitCode = 1;
                    return;
                }
                await FinishTurnAsync().ConfigureAwait(false);
                await WriteBusyConflictAsync(ex, WriteDiagnosticLineAsync,
                    tgMode ? (runId, status) => RunOnTgUi(() => tgWindow!.Hints.SetWorkspaceBusy(runId, status)) : null)
                    .ConfigureAwait(false);
            }
            catch (AgentApiException ex)
            {
                if (!hostAccepted)
                {
                    HoldSubmissionForRetry(submission);
                }
                else if (submission is not null)
                {
                    foreach (var image in submission.Images)
                    {
                        DeleteTempFile(image);
                    }
                }

                if (!presetCommitted
                    && !await _runStartCore.RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
                {
                    await FinishTurnAsync().ConfigureAwait(false);
                    dispatcherExitCode = 1;
                    return;
                }

                // Pre-run rejections (missing credential, no model) already name
                // themselves: one error line, no supplementary text. A malformed create
                // response is different: the Host may have accepted a run we cannot bind,
                // so fail closed instead of leaving the operator in an ambiguous REPL.
                await FinishTurnAsync().ConfigureAwait(false);
                await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Err(CliErrorText.Format(ex))).ConfigureAwait(false);
                if (string.Equals(ex.ErrorCode, "invalid_response", StringComparison.Ordinal))
                {
                    dispatcherExitCode = 1;
                }
            }
            catch (Exception ex)
            {
                if (!hostAccepted)
                {
                    HoldSubmissionForRetry(submission);
                }
                else if (submission is not null)
                {
                    foreach (var image in submission.Images)
                    {
                        DeleteTempFile(image);
                    }
                }

                if (!presetCommitted
                    && !await _runStartCore.RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
                {
                    await FinishTurnAsync().ConfigureAwait(false);
                    dispatcherExitCode = 1;
                    return;
                }
                await FinishTurnAsync().ConfigureAwait(false);
                await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Err($"run_error: {ex.Message}")).ConfigureAwait(false);
            }
            finally
            {
                transfer?.Dispose();
                runSettlement?.Dispose();
            }
            await _out.FlushAsync().ConfigureAwait(false);
        }

        async Task<bool> HandleSlashLineAsync(string slashLine)
        {
            if (!cmd.Json && !tgMode)
            {
                await _out.WriteLineAsync(Tui.ChatPromptEcho.Format(slashLine)).ConfigureAwait(false);
            }
            var parts = slashLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var slashCmd = parts[0].ToLowerInvariant();
            var arg = parts.Length > 1 ? parts[1].Trim() : null;

            if (slashCmd == "/preset")
            {
                await ChangePresetAsync(arg).ConfigureAwait(false);
                return true;
            }

            if (slashCmd == "/subagents")
            {
                await ShowSubagentsAsync(arg, runSession.ActiveRunId ?? runSession.ParkedRunId ?? runSession.FollowableRunId,
                    cmd.Json ? _out : humanOut, cmd.Json, cancellationToken).ConfigureAwait(false);
                return true;
            }
            if (slashCmd is "/exit")
            {
                await humanOut.WriteLineAsync(Tui.TuiPalette.DimPaint("Exiting interactive terminal. Goodbye!")).ConfigureAwait(false);
                await humanOut.FlushAsync().ConfigureAwait(false);
                dispatcherExitCode = 0;
                return true;
            }
            if (slashCmd == "/update")
            {
                var updateArguments = arg?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
                // TUI input is owned by its editor; never read Console.In here.
                if (!updateArguments.Contains("--yes", StringComparer.Ordinal) && !updateArguments.Contains("--check", StringComparer.Ordinal))
                    updateArguments = [.. updateArguments, "--check"];
                var result = await UpdateCommands.RunAsync(updateArguments, humanOut, _err, cancellationToken).ConfigureAwait(false);
                if (result == 10) dispatcherExitCode = 0;
                return true;
            }
            if (slashCmd == "/retry")
            {
                if (submissions.Retryable is null)
                {
                    await WriteDiagnosticLineAsync(humanOut, "no held submission to retry").ConfigureAwait(false);
                    return true;
                }

                var retry = submissions.Retryable;
                await HandlePromptLineAsync(retry.Prompt, cancellationToken, retry).ConfigureAwait(false);
                return true;
            }
            if (slashCmd is "/clear")
            {
                turn = 0;

                lastTotalTokens = null;
                usage.BeginTurn();
                if (tgMode)
                {
                    // Clear the TG transcript and its header usage cluster in place; the
                    // logical renderer keeps tool counters and retained details of its own,
                    // so reset it too instead of only the painted view.
                    RunOnTgUi(() => tgWindow!.ResetTranscript());
                }
                else if (!cmd.Json)
                {
                    await humanOut.WriteLineAsync("transcript reset").ConfigureAwait(false);
                }

                sessionRenderer.Transcript.ClearView();
                await humanOut.FlushAsync().ConfigureAwait(false);
                return true;
            }
            if (await HandleSessionWorkflowAsync(slashCmd, arg).ConfigureAwait(false))
            {
                await RefreshModelSelectionAsync().ConfigureAwait(false);
                return true;
            }
            if (await _slashCommands.TryHandleAsync(slashCmd, arg, sessionId, cmd.Preset, canonicalWorkspace, cancellationToken).ConfigureAwait(false))
            {
                await RefreshModelSelectionAsync().ConfigureAwait(false);
                return true;
            }
            if (slashCmd is "/release")
            {
                var result = await ChatRunRelease.ReleaseAsync(
                    _agentClient, runSession, sessionId, arg, cancellationToken).ConfigureAwait(false);
                if (result.ReleasedRunId is { } released)
                    sessionRenderer.Transcript.ForgetParkHistory(released);
                await WriteDiagnosticLineAsync(result.IsError ? _err : humanOut, result.Message).ConfigureAwait(false);
                return true;
            }
            if (slashCmd is "/follow")
            {
                await FollowRetainedRunAsync(arg).ConfigureAwait(false);
                await _out.FlushAsync().ConfigureAwait(false);
                return true;
            }
            var custom = CustomPromptCommands.Find(
                slashCmd, canonicalWorkspace, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (custom is not null)
            {
                await HandlePromptLineAsync(CustomPromptCommands.Expand(custom, arg), cancellationToken).ConfigureAwait(false);
                return true;
            }
            var customNames = CustomPromptCommands.List(canonicalWorkspace, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var customHint = customNames.Count == 0 ? string.Empty : " Custom: " + string.Join(' ', customNames.Select(name => "/" + name)) + ".";
            await WriteDiagnosticLineAsync(_err, $"unknown_command: {slashCmd}. Type /help for assistance.{customHint}").ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Report-only slash commands print into the read-only output window instead of
        /// scrolling straight into the transcript. Both writers the command can reach are
        /// pointed at a capture buffer for the run, so the window shows exactly what the
        /// command printed; when it closes the same rows are written to the transcript, so
        /// the report is still there to scroll back to. Sequential mode keeps printing
        /// inline: there is no window over a scrolling region to float above.
        /// </summary>
        async Task<bool> HandleSlashLineWithDocumentAsync(string slashLine)
        {
            var command = slashLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
            if (!tgMode || !Tui.ChatCommandCatalog.IsReportCommand(command))
            {
                return await HandleSlashLineAsync(slashLine).ConfigureAwait(false);
            }

            var capture = new StringWriter();
            var previousWidth = _slashCommands.PanelWidth;
            _slashCommands.PanelWidth = Math.Max(TuiPanel.MinWidth,
                Math.Min(ChatRenderWidth(), Math.Min(TuiCard.MaxWidth, tgWindow!.Viewport.Width - 4) - TuiCard.PadX * 3 - 2));
            var transcript = humanOut;
            humanOut = capture;
            _slashCommands.HumanOut = capture;
            var handled = false;
            try
            {
                handled = await HandleSlashLineAsync(slashLine).ConfigureAwait(false);
            }
            finally
            {
                humanOut = transcript;
                _slashCommands.HumanOut = transcript;
                _slashCommands.PanelWidth = previousWidth;
            }

            var body = capture.ToString()
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            if (body.Count > 0)
            {
                await TgPanelRequests.RequestDocumentAsync(
                    tgWindow!,
                    command.TrimStart('/'),
                    body,
                    cancellationToken).ConfigureAwait(false);
                await transcript.WriteLineAsync(string.Join('\n', body)).ConfigureAwait(false);
                await transcript.FlushAsync().ConfigureAwait(false);
            }

            return handled;
        }
    }
}
