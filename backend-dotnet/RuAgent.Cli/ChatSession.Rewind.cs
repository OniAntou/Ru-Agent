using System.Text.Json;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        private bool historyReloadRequired;

        private ChatHistoryBranch? pendingHistoryBranch;

        private async Task RewindHistoryAsync(string? argument, CancellationToken ct)
        {
            if (argument?.Trim() == "cancel")
            {
                pendingHistoryBranch = null;
                await WriteDiagnosticLineAsync(humanOut, "history branch cancelled · original conversation active");
                return;
            }
            if (!await RequireSavedSessionAsync().ConfigureAwait(false)) return;
            if (historyReloadRequired) return;
            var owner = sessionId;
            try
            {
                var plan = await new ChatHistoryWorkflow(_sessionClient, _agentClient).SelectAsync(owner, argument,
                    () => owner == sessionId && !runSession.IsTurnActive,
                    tgMode && !cmd.Json ? (list, token) => TgPanelRequests.RequestChoiceAsync(tgWindow!, list, token) : null,
                    tgMode && !cmd.Json ? (title, rows, token) => TgPanelRequests.RequestDocumentAsync(tgWindow!, title, rows, token) : null,
                    cmd.Json ? _out : humanOut, cmd.Json, ct,
                    new[] { runSession.ActiveRunId, runSession.ParkedRunId, runSession.FollowableRunId }.OfType<string>().ToArray());
                if (plan is null || owner != sessionId) return;
                if (tgMode)
                {
                    var staged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    RunOnTgUi(() =>
                    {
                        if (ct.IsCancellationRequested) { staged.TrySetCanceled(ct); return; }
                        if (owner != sessionId) { staged.TrySetResult(false); return; }
                        // Do not overwrite text typed while the asynchronous picker was open.
                        if (!string.IsNullOrEmpty(tgWindow!.Composer.Input.Text)) { staged.TrySetResult(false); return; }
                        pendingHistoryBranch = plan;
                        tgWindow.Composer.Input.Text = plan.Draft ?? "";
                        staged.TrySetResult(true);
                    });
                    if (!await staged.Task.WaitAsync(ct)) throw new AgentApiException("history_draft_busy", "Keep or clear your current draft, then select history again.");
                }
                else pendingHistoryBranch = plan;
                if (cmd.Json) await _out.WriteLineAsync(JsonSerializer.Serialize(new { v = 1, type = "history_branch_staged", source_session_id = owner,
                    keep_messages = plan.KeepMessages, draft = plan.Draft, files_restored = false }));
                else
                {
                    await WriteDiagnosticLineAsync(humanOut, "branch ready · next message creates it · current files · /history cancel to discard");
                    if (!tgMode && plan.Draft is not null) await WriteDiagnosticLineAsync(humanOut, "Saved prompt: " + ChatDisplayText.Sanitize(plan.Draft, 1000));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var message = ex is AgentApiException api ? CliErrorText.Format(api) : CliErrorText.ShortMessage(ex.Message);
                await WriteDiagnosticLineAsync(_err, "history_error: " + ChatDisplayText.Sanitize(message, 256));
            }
        }

        private void ApplyRewoundHistory(JsonElement root)
        {
            var replay = ChatSessionReplay.Parse(root);
            runSession.ResetSession();
            inputWorkflow = null;
            historyReloadRequired = false;
            pendingHistoryBranch = null;
            sessionTitle = replay.Title;
            sessionTitleProbes = 0;
            turn = 0;
            lastTotalTokens = null;
            usage = new TuiUsageTracker();
            ClearSubagentStrip();
            sessionRenderer = CreateSessionRenderer(ResolveRenderWriter());
            sessionRenderer.Width = ChatRenderWidth();
            if (tgMode) RunOnTgUi(() =>
            {
                tgWindow!.ResetTranscript();
                tgWindow.Hints.ClearWorkspaceBusy();
                tgWindow.Header.SetTitle(sessionTitle ?? "");
                tgWindow.Composer.SetMeta(cmd.Preset, activeModel, backendName ?? "(provider unknown)");
            });
            if (!cmd.Json) RenderSessionReplay(sessionId, replay);
        }

        private async Task ReloadUncertainHistoryAsync(CancellationToken ct)
        {
            try
            {
                var root = await _sessionClient.GetSessionAsync(sessionId, ct).ConfigureAwait(false);
                ChatRewindWorkflow.ReadMessages(root, sessionId);
                ApplyRewoundHistory(root);
                if (submissions.Retryable is { } held)
                {
                    submissions.Restore(held.Images, held.Texts);
                    if (tgMode) RunOnTgUi(() =>
                    {
                        if (string.IsNullOrEmpty(tgWindow!.Composer.Input.Text)) tgWindow.Composer.Input.Text = held.Prompt;
                    });
                }
                submissions.Accept();
                tgLoop?.FinishHistoryReload();
                RefreshAttachmentLabels();
                await WriteDiagnosticLineAsync(humanOut, "conversation reloaded · pending attachments preserved");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                await WriteDiagnosticLineAsync(_err, "history_reload_failed: " + ChatDisplayText.Sanitize(CliErrorText.ShortMessage(ex.Message), 200));
            }
        }

        private string? RewindSubmissionGuard(string line)
        {
            if (!historyReloadRequired) return null;
            var command = line.TrimStart().Split(' ', 2)[0].ToLowerInvariant();
            return command is "/new" or "/sessions" or "/help" or "/status" or "/exit" or "/tasks" or "/changes" or "/history"
                ? null : $"history_reload_required: use /sessions id:{sessionId} before sending";
        }
    }
}
