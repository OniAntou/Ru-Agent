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
        /// Reprints the part of the answer the deltas never carried.
        ///
        /// The live event hub has no replay buffer: a delta published while the client is
        /// still subscribing is dropped, and because deltas are ordered what is lost is the
        /// head of the reply. The run's own durable <c>assistanttext</c> payload is complete,
        /// so the gap is recoverable rather than lost. This mirrors what the WinUI surface
        /// already does by reloading session history when a run ends.
        ///
        /// Only a suffix is reprinted, and only when the streamed text is a true prefix of
        /// the payload. Anything else -- a shorter payload, a rewritten answer, a payload
        /// that is not assistant text -- is left alone rather than guessed at, because a
        /// terminal cannot unprint.
        /// </summary>
        async Task RecoverMissingAssistantTextAsync(
            string runId,
            IReadOnlyList<string> payloadRefs,
            CancellationToken recoveryToken)
        {
            if (payloadRefs.Count == 0)
            {
                Tui.TuiDebug.Log("recover", "no step payload refs seen on the stream");
                return;
            }

            if (runSession.HasSuppressedReply)
            {
                // The turn streamed prose the host suppressed for recovery, so the
                // streamed text and the durable answer no longer align: the gap check
                // cannot tell a lost tail from a superseded reply. Stand down rather
                // than report truncation or reprint text the user must never see.
                Tui.TuiDebug.Log("recover", "skipped: the turn carried a host-suppressed reply");
                return;
            }

            var expected = new StringBuilder();
            var completeSteps = new List<string>();
            IReadOnlyList<AgentPayloadContent> fetched;
            try
            {
                fetched = await ChatAssistantPayloadReader.ReadAsync(payloadRefs,
                    (payloadRef, token) => _agentClient.GetPayloadContentAsync(runId, payloadRef, token),
                    recoveryToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await WriteDiagnosticLineAsync(_err, $"assistant_text_recovery_failed: {ex.Message}").ConfigureAwait(false);
                return;
            }

            var prose = Tui.AssistantTextReconciler.ProsePayloads(fetched);
            var dropped = fetched.Count(part => part.Content.Length > 0) - prose.Count;
            if (dropped > 0)
            {
                Tui.TuiDebug.Log("recover", $"dropped {dropped} step-record payloads");
            }

            foreach (var text in prose)
            {
                expected.Append(text);
                completeSteps.Add(text);
            }

            if (expected.Length == 0)
            {
                Tui.TuiDebug.Log("recover", $"no durable text behind {payloadRefs.Count} refs");
                return;
            }

            var full = TuiText.SanitizeUntrusted(expected.ToString());
            var streamed = TuiText.SanitizeUntrusted(sessionRenderer.Transcript.StreamedAssistantText);
            Tui.TuiDebug.Log("recover", $"streamed {streamed.Length} of {full.Length} chars");
            var decision = Tui.AssistantTextReconciler.Decide(streamed, full);
            if (decision == Tui.AssistantTextRecovery.Complete
                || decision == Tui.AssistantTextRecovery.None)
            {
                return;
            }

            if (decision == Tui.AssistantTextRecovery.ReportTruncation)
            {
                var replacement = string.Join("\n\n", completeSteps.Select(TuiText.SanitizeUntrusted));
                if (await sessionRenderer.Transcript.RecoverAssistantTurnAsync(replacement).ConfigureAwait(false))
                    return;
                // Both output paths append text. Report missing history rather than
                // clearing the screen or appending an older prefix after a newer tail.
                await WriteDiagnosticLineAsync(
                        _err,
                        $"assistant_text_truncated: {streamed.Length} of {full.Length} characters reached this terminal. The full reply is in the session; /status or reopen it to read all of it.")
                    .ConfigureAwait(false);
                Tui.TuiDebug.Log("recover", $"head missing: {streamed.Length} of {full.Length}");
                return;
            }

            var missing = Tui.AssistantTextReconciler.SuffixFor(decision, streamed, full);
            if (missing.Length == 0)
            {
                return;
            }

            // The tracked append point already continues the streamed row; rewinding to
            // the row start would overwrite it.
            sessionRenderer.Transcript.AppendAssistant(missing);
            sessionRenderer.Flush();
            Tui.TuiDebug.Log("recover", $"reprinted {missing.Length} chars of {full.Length}");
        }

        async Task FinishTurnAsync()
        {
            await RefreshSessionTitleAsync().ConfigureAwait(false);
            sessionRenderer.EndTurn();
            runSession.EndTurn();
        }

        /// <summary>
        /// Reads an <c>ask_user</c> request payload referenced by an input.requested
        /// frame so the notice can show the question and its suggested answers. A
        /// payload that cannot be read still parks the run; the notice then names only
        /// the input id, which is all the answer command needs.
        /// </summary>
        /// <summary>
        /// Follows one run to its terminal frame, rendering every durable and transient
        /// event and recovering a lost assistant head once it ends. <paramref name="afterSequence"/>
        /// is the durable cursor to resume from: an interactive turn starts at 0, while a
        /// continued run starts at the parked run's LastSequence so the resume never
        /// replays output the terminal already printed.
        /// </summary>
        async Task FollowRunAsync(string runId, long afterSequence, CancellationToken ct)
        {
            SseFrame? parkedTransition = null;
            async Task OnFrameAsync(SseFrame frame, CancellationToken frameCt)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A parked run emits no further driving frames; the server holds the SSE
                // stream open, which used to read as an eternally spinning "Thinking" row.
                // Release the stream instead: the catch treats a set runSession.ParkedRunId as a
                // pause, not a user interruption, so no cancel is posted.
                if (TryReadParkedTransition(frame, out var parkedRun))
                {
                    parkedTransition = frame;
                    runSession.ParkActiveRun(parkedRun!);
                }

                if (cmd.Json)
                {
                    NdjsonEventRenderer.RenderFrame(frame, _out);

                    // --json never prompts for an approval: there is no composer to ask in,
                    // and reading stdin here would consume the scripted consumer's next
                    // prompt. The run stays waiting server-side and the operator answers
                    // out of band, so this notice goes to stderr and the streaming turn
                    // ends instead of spinning on a stream that will not advance.
                    if (!frame.IsTransient && frame.Data is not null && frame.Data.Contains("\"approval.requested\"", StringComparison.Ordinal))
                    {
                        HarnessSafeEventEnvelope? approvalEnvelope = null;
                        try
                        {
                            var candidate = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
                            if (candidate is { EventType: "approval.requested" } && !string.IsNullOrEmpty(candidate.ApprovalId))
                            {
                                approvalEnvelope = candidate;
                            }
                        }
                        catch (JsonException)
                        {
                            // Non-conforming frame payload; the NDJSON line above already
                            // carried the event and there is no id to point the operator at.
                        }

                        if (approvalEnvelope is not null)
                        {
                            var targetRunId = !string.IsNullOrEmpty(approvalEnvelope.RunId) ? approvalEnvelope.RunId : runId;
                            await WriteDiagnosticLineAsync(
                                    _err,
                                    $"approval_required: run {targetRunId} is waiting for approval {approvalEnvelope.ApprovalId}. Answer it with 'ru agent approve {targetRunId} {approvalEnvelope.ApprovalId}' or deny it with 'ru agent deny {targetRunId} {approvalEnvelope.ApprovalId}'.")
                                .ConfigureAwait(false);
                            await _out.FlushAsync().ConfigureAwait(false);
                            await _err.FlushAsync().ConfigureAwait(false);
                            throw new RunWaitingApprovalException(targetRunId, approvalEnvelope.ApprovalId!);
                        }
                    }

                    // The ask_user park is answered out of band for the same reason an
                    // approval is: --json must not consume the scripted consumer's stdin.
                    if (!frame.IsTransient && frame.Data is not null && frame.Data.Contains("\"input.requested\"", StringComparison.Ordinal))
                    {
                        HarnessSafeEventEnvelope? inputEnvelope = null;
                        try
                        {
                            var candidate = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
                            if (candidate is { EventType: "input.requested" })
                            {
                                inputEnvelope = candidate;
                            }
                        }
                        catch (JsonException)
                        {
                            // Non-conforming frame payload; the NDJSON line above already
                            // carried the event and there is no id to point the operator at.
                        }

                        if (inputEnvelope is not null)
                        {
                            var targetRunId = !string.IsNullOrEmpty(inputEnvelope.RunId) ? inputEnvelope.RunId : runId;
                            var request = await ReadInputRequestAsync(targetRunId, inputEnvelope.PayloadRef, frameCt).ConfigureAwait(false);
                            if (request is { } parkedInput)
                            {
                                await WriteDiagnosticLineAsync(
                                        _err,
                                        DescribeInputRequest(targetRunId, parkedInput.InputId, parkedInput.Question, parkedInput.Options))
                                    .ConfigureAwait(false);
                                await _out.FlushAsync().ConfigureAwait(false);
                                await _err.FlushAsync().ConfigureAwait(false);
                                throw new RunWaitingInputException(targetRunId, parkedInput.InputId);
                            }
                        }
                    }
                }
                else
                {
                    sessionRenderer.RenderFrame(frame);

                    if (!frame.IsTransient && frame.Data is not null && frame.Data.Contains("\"approval.requested\"", StringComparison.Ordinal))
                    {
                        try
                        {
                            var envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
                            if (envelope is not null && envelope.EventType == "approval.requested" && !string.IsNullOrEmpty(envelope.ApprovalId))
                            {
                                var targetRunId = !string.IsNullOrEmpty(envelope.RunId) ? envelope.RunId : runId;
                                var decision = await RequestApprovalAsync(Tui.ApprovalPrompt.For(envelope.SafeSummary, offerDirectory: true), frameCt).ConfigureAwait(false);

                                if (decision == Tui.ApprovalPrompt.Deny)
                                {
                                    await _agentClient.DenyAsync(targetRunId, envelope.ApprovalId, frameCt).ConfigureAwait(false);
                                    sessionRenderer.Note("denied · this tool call aborts", Tui.TuiToken.Err);
                                }
                                else
                                {
                                    await _agentClient.ApproveAsync(targetRunId, envelope.ApprovalId, decision, frameCt).ConfigureAwait(false);
                                    var scope = decision == Tui.ApprovalPrompt.AllowDirectory ? "folder for this session"
                                        : decision == Tui.ApprovalPrompt.AllowAlways ? "remembered for this project"
                                        : "once";
                                    sessionRenderer.Note("approved · " + scope, Tui.TuiToken.Ok);
                                }

                                await _out.FlushAsync().ConfigureAwait(false);
                            }
                        }
                        catch (OperationCanceledException) when (frameCt.IsCancellationRequested)
                        {
                            // The operator cancelled the turn while the approval was open.
                            throw;
                        }
                        catch (RunParkedException)
                        {
                            // The TG escape above: the notice is already in the transcript and
                            // the run is deliberately left waiting server-side.
                            throw;
                        }
                        catch (Exception ex)
                        {
                            await WriteDiagnosticLineAsync(_err, $"inline_approval_error: {ex.Message}").ConfigureAwait(false);
                        }
                    }

                    if (!frame.IsTransient && frame.Data is not null && frame.Data.Contains("\"input.requested\"", StringComparison.Ordinal))
                    {
                        try
                        {
                            var envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
                            if (envelope is not null && envelope.EventType == "input.requested")
                            {
                                if (tgMode)
                                {
                                    await AnswerInputInTgAsync(envelope, runId, frameCt).ConfigureAwait(false);
                                    return;
                                }
                                var targetRunId = !string.IsNullOrEmpty(envelope.RunId) ? envelope.RunId : runId;
                                var request = await ReadInputRequestAsync(targetRunId, envelope.PayloadRef, frameCt).ConfigureAwait(false);
                                if (request is { } parkedInput)
                                {
                                    // The question is the model's, not the operator's; the
                                    // notice points at the answer command instead of reading
                                    // a prompt that would interleave with the next turn.
                                    sessionRenderer.Note(
                                        DescribeInputRequest(targetRunId, parkedInput.InputId, parkedInput.Question, parkedInput.Options),
                                        Tui.TuiToken.Warn);
                                    await _out.FlushAsync().ConfigureAwait(false);
                                    throw new RunWaitingInputException(targetRunId, parkedInput.InputId);
                                }
                            }
                        }
                        catch (OperationCanceledException) when (frameCt.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (RunParkedException)
                        {
                            // The notice is already in the transcript and the run is
                            // deliberately left waiting server-side.
                            throw;
                        }
                        catch (Exception ex)
                        {
                            await WriteDiagnosticLineAsync(_err, $"inline_input_error: {ex.Message}").ConfigureAwait(false);
                        }
                    }

                    RecordAssistantStepPayload(frame);
                    ApplyUsageFrame(frame);
                    ApplyUsageEstimate(frame);
                    await ApplySubagentChangeAsync(frame).ConfigureAwait(false);
                    ApplyCompactionFrame(frame);
                }
                await _out.FlushAsync().ConfigureAwait(false);
            }

            try
            {
                await StreamRunEventsResumedAsync(runId, OnFrameAsync, ct, afterSequence).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested
                && string.Equals(runSession.ParkedRunId, runId, StringComparison.Ordinal))
            {
                // The state frame precedes the reason in the same commit. Recover only
                // its exact companion through finite replay before returning to the composer.
                if (parkedTransition is { DurableSequence: { } parkSequence }
                    && ChatParkReasonRecovery.NeedsReplay(parkedTransition))
                {
                    var reason = await ChatParkReasonRecovery.ReadAsync(
                        _sseClient, runId, parkSequence, cancellationToken).ConfigureAwait(false);
                    if (reason is not null) await OnFrameAsync(reason, cancellationToken).ConfigureAwait(false);
                }
                // Parking deliberately cancels the subscription. Preserve the
                // durable-text recovery leg before the caller winds down the turn.
                sessionRenderer.Flush();
                await RecoverMissingAssistantTextAsync(runId, runSession.AssistantPayloads, ct).ConfigureAwait(false);
                throw;
            }
            sessionRenderer.Flush();
            // The recovery reprints a lost suffix through the same surface/writer the turn
            // streamed through, so it works for both engines; only the truncation repaint is
            // sequential-only (the TG transcript cannot rebuild what it already painted).
            await RecoverMissingAssistantTextAsync(runId, runSession.AssistantPayloads, ct).ConfigureAwait(false);

            await _out.FlushAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Shared wind-down for a cancelled turn: distinguishes a user interruption (the
        /// server run is cancelled so it releases the session) from a run that
        /// parked itself (nothing is executing; the user resumes it explicitly).
        /// </summary>
        async Task ReportInterruptionAsync()
        {
            var cancelRunId = runSession.InterruptedRunId;
            await FinishTurnAsync().ConfigureAwait(false);
            if (cancelRunId is null)
            {
                if (runSession.ParkedRunId is null)
                {
                    await humanOut.WriteLineAsync(Tui.TuiPalette.Warn("Run interrupted by user.")).ConfigureAwait(false);
                }

                // A parked run is the user's to resume: there is nothing to cancel, and the
                // transcript already printed the single "run parked ... Continue" notice
                // for it, so printing another line here only duplicated that guidance.
                return;
            }

            try
            {
                await _agentClient.CancelAsync(cancelRunId, CancellationToken.None).ConfigureAwait(false);
                // The server confirmed the cancellation; the run is terminal and no longer
                // blocks /preset or the next create.
                runSession.ForgetCompletedRun();
                await humanOut.WriteLineAsync(Tui.TuiPalette.Warn($"Run interrupted by user · cancelled {cancelRunId}.")).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await humanOut.WriteLineAsync(Tui.TuiPalette.Warn("Run interrupted by user.")).ConfigureAwait(false);
                await humanOut.WriteLineAsync(Tui.TuiPalette.Err($"run_cancel_failed: {ex.Message}")).ConfigureAwait(false);
                await humanOut.WriteLineAsync(Tui.TuiPalette.DimPaint($"run {cancelRunId} may still be running; release it with 'ru agent cancel {cancelRunId}'.")).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Resumes a parked run the way the GUI's typed Continue does: read the parked
        /// run's durable cursor, post the continue, then follow the run from that cursor
        /// as if it were a fresh turn. Without a tracked parked run this is informational,
        /// never an error.
        /// </summary>
        async Task ContinueParkedRunAsync()
        {
            var targetRunId = runSession.ParkedRunId;
            if (string.IsNullOrWhiteSpace(targetRunId))
            {
                await humanOut.WriteLineAsync(Tui.TuiPalette.DimPaint("no parked run · nothing to continue")).ConfigureAwait(false);
                await _out.FlushAsync().ConfigureAwait(false);
                return;
            }

            // The parked turn already rendered durable frames up to LastSequence; the
            // continue call's own "continued" event is the first frame after it. Resuming
            // from 0 would replay the whole turn into a terminal that cannot unprint.
            GetAgentRunResponse run;
            try
            {
                run = await _agentClient.GetRunAsync(targetRunId, cancellationToken).ConfigureAwait(false);
            }
            catch (AgentApiException ex)
            {
                await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Err(CliErrorText.Format(ex))).ConfigureAwait(false);
                await _out.FlushAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                // A transport failure must not escape the REPL: report it bounded and keep
                // the prompt (and the parked tracking) intact.
                await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Err(
                    $"continue_error: {CliErrorText.ShortMessage(ex.Message)}")).ConfigureAwait(false);
                await _out.FlushAsync().ConfigureAwait(false);
                return;
            }

            if (!string.Equals(run.SessionId, sessionId, StringComparison.Ordinal))
            {
                await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Err(
                    $"run_session_mismatch: run belongs to {run.SessionId}; resume that session with /sessions id:{run.SessionId} before continuing it.")).ConfigureAwait(false);
                await _out.FlushAsync().ConfigureAwait(false);
                return;
            }

            if (run.TerminalAt is not null || IsTerminalRunStatus(run.Status))
            {
                await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Err(
                    $"run_not_runnable: run {targetRunId} is {run.Status} and cannot be continued.")).ConfigureAwait(false);
                runSession.ForgetParked(targetRunId);

                await _out.FlushAsync().ConfigureAwait(false);
                return;
            }

            // A retained target that is no longer parked (running, waiting_approval,
            // waiting_workspace_trust) is being driven elsewhere or waits on another
            // decision: the resume POST could only earn a 409.
            if (!IsParkedRunStatus(run.Status))
            {
                await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Err(
                    $"run_not_parked: run {targetRunId} is {run.Status} and not waiting to continue.")).ConfigureAwait(false);
                await _out.FlushAsync().ConfigureAwait(false);
                return;
            }

            await ShowRetainedRunProviderAsync(run).ConfigureAwait(false);

            runSession.BeginContinue(targetRunId, cancellationToken);
            sessionRenderer.Width = ChatRenderWidth();
            sessionRenderer.Status.SetCounters(lastTotalTokens, 0);
            sessionRenderer.BeginTurn();

            try
            {
                await _agentClient.ContinueAsync(targetRunId, runSession.Token).ConfigureAwait(false);
                // Only a confirmed Continue releases this run's parked identity.
                runSession.ForgetParked(targetRunId);

                await FollowRunAsync(targetRunId, run.LastSequence, runSession.Token).ConfigureAwait(false);
                await FinishTurnAsync().ConfigureAwait(false);
                // A completed Continue is no longer a retained run: leave the identity
                // clean so /preset and the next create are not blocked by a terminal run.
                runSession.ForgetCompletedRun();
            }
            catch (OperationCanceledException)
            {
                await ReportInterruptionAsync().ConfigureAwait(false);
            }
            catch (RunParkedException)
            {
                // The --json approval escape: the notice is already on stderr and the
                // run stays waiting server-side. Wind the turn down and let the caller
                // exit the chat instead of settling for a "continue_error" line.
                await FinishTurnAsync().ConfigureAwait(false);
                runSession.RememberFollowable(targetRunId);
                throw;
            }
            catch (AgentApiException ex)
            {
                await FinishTurnAsync().ConfigureAwait(false);
                await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Err(CliErrorText.Format(ex))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await FinishTurnAsync().ConfigureAwait(false);
                await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Err($"continue_error: {ex.Message}")).ConfigureAwait(false);
            }

            await _out.FlushAsync().ConfigureAwait(false);
        }

        async Task FollowRetainedRunAsync(string? runIdArg)
        {
            if (runSession.IsTurnActive)
            {
                await WriteDiagnosticLineAsync(_err, "run_busy: finish or cancel the active run before following another run").ConfigureAwait(false);
                return;
            }

            var targetRunId = string.IsNullOrWhiteSpace(runIdArg)
                ? runSession.FollowableRunId ?? runSession.ActiveRunId
                : runIdArg.Trim();
            if (string.IsNullOrWhiteSpace(targetRunId))
            {
                targetRunId = await FindNewestFollowableRunAsync().ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(targetRunId))
            {
                await WriteDiagnosticLineAsync(humanOut, "no active run to follow · type Continue for a parked run").ConfigureAwait(false);
                return;
            }

            GetAgentRunResponse run;
            try
            {
                run = await _agentClient.GetRunAsync(targetRunId, cancellationToken).ConfigureAwait(false);
            }
            catch (AgentApiException ex)
            {
                await WriteDiagnosticLineAsync(_err, CliErrorText.Format(ex)).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                await WriteDiagnosticLineAsync(_err, $"follow_error: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
                return;
            }

            if (!string.Equals(run.SessionId, sessionId, StringComparison.Ordinal))
            {
                await WriteDiagnosticLineAsync(
                    _err,
                    $"run_session_mismatch: run belongs to {run.SessionId}; resume that session with /sessions id:{run.SessionId} before following it").ConfigureAwait(false);
                return;
            }

            if (!ChatRunWorkflow.IsFollowable(run))
            {
                var hint = IsParkedRunStatus(run.Status)
                    ? $"run {targetRunId} is parked · use 'ru agent continue {targetRunId}'"
                    : $"run_not_followable: run {targetRunId} is {run.Status}";
                await WriteDiagnosticLineAsync(_err, hint).ConfigureAwait(false);
                return;
            }

            runSession.RememberFollowable(targetRunId);
            var pendingApproval = default(RuAgent.Protocol.Harness.HarnessSafeEventEnvelope);
            var pendingInput = default(RuAgent.Protocol.Harness.HarnessSafeEventEnvelope);
            if (string.Equals(run.Status, "waiting_input", StringComparison.OrdinalIgnoreCase))
            {
                var recovery = await ChatRunWorkflow.ReadPendingInputAsync(_sseClient, run, cancellationToken).ConfigureAwait(false);
                if (recovery.Error is not null || recovery.Pending is null)
                {
                    await WriteDiagnosticLineAsync(_err, recovery.Error ?? "input_recovery_incomplete: pending question unavailable").ConfigureAwait(false);
                    return;
                }
                if (!tgMode || cmd.Json)
                {
                    var request = await ReadInputRequestAsync(targetRunId, recovery.Pending.PayloadRef, cancellationToken).ConfigureAwait(false);
                    await WriteDiagnosticLineAsync(_err, request is { } input
                        ? DescribeInputRequest(targetRunId, input.InputId, input.Question, input.Options)
                        : "input_request_unavailable: pending question unavailable").ConfigureAwait(false);
                    return;
                }
                var refreshed = await _agentClient.GetRunAsync(targetRunId, cancellationToken).ConfigureAwait(false);
                if (!ChatRunWorkflow.SameSnapshot(run, refreshed))
                {
                    await WriteDiagnosticLineAsync(_err, "input_stale: run changed during question recovery · use /follow again").ConfigureAwait(false);
                    return;
                }
                pendingInput = recovery.Pending;
            }
            if (string.Equals(run.Status, "waiting_approval", StringComparison.OrdinalIgnoreCase))
            {
                var recovery = await ChatRunWorkflow.ReadPendingApprovalAsync(_sseClient, run, cancellationToken).ConfigureAwait(false);
                if (recovery.Error is not null || recovery.Pending is null)
                {
                    await WriteDiagnosticLineAsync(_err, recovery.Error ?? "approval_recovery_incomplete: pending request was unavailable").ConfigureAwait(false);
                    return;
                }

                var refreshed = await _agentClient.GetRunAsync(targetRunId, cancellationToken).ConfigureAwait(false);
                if (!ChatRunWorkflow.SameSnapshot(run, refreshed)
                    || !string.Equals(refreshed.Status, "waiting_approval", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteDiagnosticLineAsync(_err, $"run_changed: run {targetRunId} changed during approval recovery; use /follow again").ConfigureAwait(false);
                    return;
                }

                pendingApproval = recovery.Pending;
                if (cmd.Json)
                {
                    await WriteDiagnosticLineAsync(
                        _err,
                        $"approval_required: run {targetRunId} is waiting for approval {pendingApproval.ApprovalId}. Answer it with 'ru agent approve {targetRunId} {pendingApproval.ApprovalId}' or deny it with 'ru agent deny {targetRunId} {pendingApproval.ApprovalId}'.").ConfigureAwait(false);
                    return;
                }
            }

            await ShowRetainedRunProviderAsync(run).ConfigureAwait(false);

            runSession.BeginFollow(targetRunId, cancellationToken);
            sessionRenderer.Width = ChatRenderWidth();
            sessionRenderer.Status.SetCounters(lastTotalTokens, 0);
            sessionRenderer.BeginTurn();

            try
            {
                if (pendingInput is not null)
                    await AnswerInputInTgAsync(pendingInput, targetRunId, runSession.Token).ConfigureAwait(false);
                if (pendingApproval is not null)
                {
                    var decision = await RequestApprovalAsync(
                        Tui.ApprovalPrompt.For(pendingApproval.SafeSummary, offerDirectory: true),
                        runSession.Token).ConfigureAwait(false);
                    var latest = await _agentClient.GetRunAsync(targetRunId, runSession.Token).ConfigureAwait(false);
                    if (!ChatRunWorkflow.SameSnapshot(run, latest)
                        || !string.Equals(latest.Status, "waiting_approval", StringComparison.OrdinalIgnoreCase))
                    {
                        sessionRenderer.Note($"run_changed: approval for {targetRunId} is no longer current · follow again", Tui.TuiToken.Warn);
                        await FinishTurnAsync().ConfigureAwait(false);
                        return;
                    }

                    if (decision == Tui.ApprovalPrompt.Deny)
                    {
                        await _agentClient.DenyAsync(targetRunId, pendingApproval.ApprovalId!, runSession.Token).ConfigureAwait(false);
                        sessionRenderer.Note("denied · this tool call aborts", Tui.TuiToken.Err);
                    }
                    else
                    {
                        await _agentClient.ApproveAsync(targetRunId, pendingApproval.ApprovalId!, decision, runSession.Token).ConfigureAwait(false);
                        var scope = decision == Tui.ApprovalPrompt.AllowDirectory ? "folder for this session"
                            : decision == Tui.ApprovalPrompt.AllowAlways ? "remembered for this project"
                            : "once";
                        sessionRenderer.Note("approved · " + scope, Tui.TuiToken.Ok);
                    }
                }

                await FollowRunAsync(targetRunId, run.LastSequence, runSession.Token).ConfigureAwait(false);
                await FinishTurnAsync().ConfigureAwait(false);
                runSession.ForgetCompletedRun();
            }
            catch (RunParkedException)
            {
                await FinishTurnAsync().ConfigureAwait(false);
                runSession.RememberFollowable(targetRunId);
            }
            catch (OperationCanceledException)
            {
                await ReportInterruptionAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await FinishTurnAsync().ConfigureAwait(false);
                await WriteDiagnosticLineAsync(_err, $"follow_error: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
            }
        }

        async Task<string?> FindNewestFollowableRunAsync()
        {
            try
            {
                return await ChatRunWorkflow.FindNewestFollowableRunIdAsync(
                    _agentClient,
                    sessionId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await WriteDiagnosticLineAsync(_err, $"follow_discovery_failed: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
            }

            return null;
        }
    }
}
