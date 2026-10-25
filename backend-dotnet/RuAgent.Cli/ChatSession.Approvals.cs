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
        /// Answers one approval. The sequential layout keeps the numbered stdin prompt;
        /// cancelling the run releases the wait instead of leaving the caller blocked.
        /// </summary>
        async Task<string> RequestApprovalAsync(Tui.ApprovalPrompt prompt, CancellationToken approvalToken)
        {
            if (tgMode)
            {
                return await RequestApprovalInTgAsync(prompt, approvalToken).ConfigureAwait(false);
            }

            // The numbered line mirrors the prompt's own choices so a folder or
            // remember answer exists in the plain layout too; anything the line
            // does not list still answers deny.
            var optionsText = string.Join(
                " / ",
                prompt.Choices.Select(choice => choice.Label));
            await _out.WriteAsync(Tui.TuiPalette.BoldPaint($"\n  ❯ Approve? [{optionsText}] (default: 2): ")).ConfigureAwait(false);
            await _out.FlushAsync().ConfigureAwait(false);
            var answer = await _in.ReadLineAsync(approvalToken).ConfigureAwait(false);
            var normalized = string.IsNullOrWhiteSpace(answer) ? "2" : answer.Trim().ToLowerInvariant();
            if (normalized is "once" or "yes" or "y")
            {
                return Tui.ApprovalPrompt.AllowOnce;
            }

            var selected = prompt.Choices.FirstOrDefault(
                choice => choice.Label.StartsWith(normalized + " ", StringComparison.Ordinal));
            return selected?.Decision ?? Tui.ApprovalPrompt.Deny;
        }

        /// <summary>
        /// The TG approval: the panel floats over the transcript and the window's loop owns
        /// the keyboard. Escape denies — which ends the tool call, not the session — exactly
        /// like the pinned-footer path, so the turn keeps streaming its settled rows.
        /// </summary>
        Task<string> RequestApprovalInTgAsync(Tui.ApprovalPrompt prompt, CancellationToken approvalToken) =>
            RunTgPanelAsync(() => tgWindow!.ShowApproval(prompt), approvalToken);

        /// <summary>The TG workspace-trust ask; Escape is a cancel, never a silent grant.</summary>
        Task<string> RequestTrustInTgAsync(Tui.ApprovalPrompt prompt, CancellationToken trustToken) =>
            RunTgPanelAsync(() => tgWindow!.ShowWorkspaceTrust(prompt), trustToken);

        /// <summary>
        /// Runs one floating panel on the Terminal.Gui loop and awaits the answer. The turn
        /// streams on a background task, so the panel is shown through the app's dispatcher;
        /// the token releases the wait when the turn is cancelled underneath it. The panel
        /// always closes in the finally, and a wind-down with the app already stopped is a
        /// no-op instead of an error.
        /// </summary>
        async Task<string> RunTgPanelAsync(Action show, CancellationToken panelToken)
        {
            await tgWindow!.ModalRequestGate.WaitAsync(panelToken).ConfigureAwait(false);
            try { return await RunTgPanelCoreAsync(show, panelToken).ConfigureAwait(false); }
            finally { tgWindow.ModalRequestGate.Release(); }
        }

        async Task<string> RunTgPanelCoreAsync(Action show, CancellationToken panelToken)
        {
            var decision = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnConfirmed(string value) => decision.TrySetResult(value);
            void OnCancelled() => decision.TrySetResult(Tui.ApprovalPrompt.Deny);

            try
            {
                tgApp!.Invoke(() =>
                {
                    if (decision.Task.IsCompleted) return;
                    tgWindow!.PanelConfirmed += OnConfirmed;
                    tgWindow.PanelCancelled += OnCancelled;
                    show();
                });
            }
            catch (NotInitializedException)
            {
                // The session wound down before the panel could open: the answer
                // is a cancel, never a silent grant.
                return Tui.ApprovalPrompt.Deny;
            }

            using var registration = panelToken.Register(() => decision.TrySetCanceled(panelToken));
            try
            {
                return await decision.Task.ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    tgApp.Invoke(() =>
                    {
                        tgWindow!.PanelConfirmed -= OnConfirmed;
                        tgWindow.PanelCancelled -= OnCancelled;
                        tgWindow.HidePanel();
                    });
                }
                catch (NotInitializedException)
                {
                    // The TG session wound down while the panel was open; there is no view
                    // left to close and the enclosing turn is ending anyway.
                }
            }
        }

        /// <summary>
        /// Asks the operator to trust the workspace while the run holds the challenge open.
        /// Only the TG frame has a composer to ask in; the sequential fallback returns null
        /// so its caller reports the bounded code. Returning without a decision is a cancel,
        /// never a silent grant.
        /// </summary>
        async Task<string?> PromptWorkspaceTrustAsync(
            Tui.ApprovalPrompt prompt,
            CancellationToken trustToken)
        {
            if (tgMode)
            {
                return await RequestTrustInTgAsync(prompt, trustToken).ConfigureAwait(false);
            }

            return null;
        }

        async Task<bool> PostTrustDecisionAsync(
            string runId,
            string trustRequestId,
            string displayHash,
            CancellationToken trustToken)
        {
            try
            {
                var trustResponse = await _agentClient.TrustAsync(
                        runId,
                        new TrustAgentRunRequest(trustRequestId, displayHash, "approve"),
                        trustToken)
                    .ConfigureAwait(false);
                if (string.Equals(trustResponse.RunId, runId, StringComparison.Ordinal)
                    && string.Equals(trustResponse.TrustRequestId, trustRequestId, StringComparison.Ordinal)
                    && RunStartCore.IsAcceptedTrustStatus(trustResponse.Status))
                {
                    return true;
                }

                await WriteDiagnosticLineAsync(_err, "workspace_trust_error: trust response does not match the challenge.").ConfigureAwait(false);
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AgentApiException ex)
            {
                await WriteDiagnosticLineAsync(_err, $"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
                return false;
            }
            catch (Exception ex)
            {
                await WriteDiagnosticLineAsync(_err, $"workspace_trust_error: {ex.Message}").ConfigureAwait(false);
                return false;
            }
        }

        /// <summary>
        /// Releases a run the operator declined to trust. The challenge cannot be answered
        /// later — it is single-use and the workspace is not on the allow list — so leaving
        /// the run parked would hold the session's active-run slot for the whole sweep
        /// interval. Trust is a pre-execution gate, so cancelling rolls back no tool effect.
        /// </summary>
        async Task CancelUntrustedRunAsync(string runId, CancellationToken trustToken)
        {
            try
            {
                await _agentClient.CancelAsync(runId, trustToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await WriteDiagnosticLineAsync(_err, $"run_cancel_failed: {ex.Message}").ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Runs the in-composer workspace-trust ask end to end: validate the binding, prompt,
        /// then post the server-issued challenge. A decline cancels the run, because the
        /// challenge is single-use and the run would otherwise sit on the session's
        /// active-run slot until the host swept it.
        /// </summary>
        async Task<(bool Trusted, bool Declined)> ResolveWorkspaceTrustInPlaceAsync(
            CreateAgentRunResponse response,
            string expectedSessionId,
            string expectedPreset,
            string workspacePath,
            bool json,
            CancellationToken trustToken)
        {
            if (!RunStartCore.IsCreateResponseBound(response, expectedSessionId, expectedPreset))
            {
                await WriteDiagnosticLineAsync(_err, "agent_create_failed: invalid create response binding.").ConfigureAwait(false);
                return (false, false);
            }

            var trustRequestId = response.TrustRequestId;
            var displayHash = response.DisplayIdentityHash;
            if (string.IsNullOrWhiteSpace(trustRequestId) || string.IsNullOrWhiteSpace(displayHash))
            {
                await WriteDiagnosticLineAsync(_err, "workspace_trust_error: trust challenge is incomplete.").ConfigureAwait(false);
                return (false, false);
            }

            var decision = await PromptWorkspaceTrustAsync(
                    Tui.ApprovalPrompt.ForWorkspaceTrust(workspacePath),
                    trustToken)
                .ConfigureAwait(false);

            if (decision is null)
            {
                // No composer to ask in, so there is no frame to protect and the bounded
                // code keeps its established stream. Never read stdin here: a scripted
                // --json consumer owns it. --json owns stdout, and TG owns the screen, so
                // the notice follows humanOut instead of the raw console writer.
                await (json ? _err : humanOut).WriteLineAsync(
                        RunStartCore.WorkspaceTrustRequiredMessage(response.RunId))
                    .ConfigureAwait(false);
                return (false, false);
            }

            if (!string.Equals(decision, Tui.ApprovalPrompt.TrustApprove, StringComparison.Ordinal))
            {
                await CancelUntrustedRunAsync(response.RunId, trustToken).ConfigureAwait(false);
                return (false, true);
            }

            var granted = await PostTrustDecisionAsync(response.RunId, trustRequestId, displayHash, trustToken)
                .ConfigureAwait(false);
            return (granted, false);
        }
    }
}
