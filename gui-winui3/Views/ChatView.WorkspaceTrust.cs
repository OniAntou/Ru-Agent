using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Controls;
using RuApp.Models;
using RuApp.Services;
using RuApp.ViewModels;
using Windows.Storage.Streams;
using System.IO;

namespace RuApp.Views
{
    public sealed partial class ChatView
    {
        /// <summary>
        /// The folder a run's session works in: the Host's session record first, then the
        /// exact path this app sent when it created the run. Never the composer's current
        /// default, which may have changed since the run was created.
        /// </summary>
        private string? ResolveRunWorkspacePath(string sessionId)
        {
            if (SessionWorkspaceResolver?.Invoke(sessionId) is { Length: > 0 } hostPath) return hostPath;
            return _requestedWorkspaceBySession.TryGetValue(sessionId, out var requested) ? requested : null;
        }

        // The card's pending answer. True trusts, false declines, null means the card
        // went away without an answer (session change, run no longer needs trust, window
        // close). A null never reaches the Host as a decision: the run stays parked.
        private TaskCompletionSource<bool?>? _trustCardWait;

        private Task<bool?> AskWorkspaceTrustAsync(WorkspaceTrustPromptPolicy.Prompt prompt)
        {
            _trustCardWait?.TrySetResult(null);
            var wait = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _trustCardWait = wait;
            TrustPanel.Show(prompt);
            return wait.Task;
        }

        private void OnTrustPanelDecisionRequested(object? sender, bool approved)
        {
            var wait = _trustCardWait;
            _trustCardWait = null;
            TrustPanel.Hide();
            wait?.TrySetResult(approved);
        }

        /// <summary>
        /// Takes the card away without answering it. Every exit that is not the user's own
        /// choice goes through here, so the pending wait always completes and the guard it
        /// holds is released.
        /// </summary>
        private void DismissWorkspaceTrustCard()
        {
            var wait = _trustCardWait;
            _trustCardWait = null;
            TrustPanel?.Hide();
            wait?.TrySetResult(null);
        }
        private async void ShowWorkspaceTrustCardAsync()
        {
            var controller = _agentController;
            var run = controller?.CurrentRun;
            if (controller is null
                || run is null
                || !string.Equals(run.SessionId, _activeSessionId, StringComparison.Ordinal)
                || !controller.NeedsWorkspaceTrust
                || XamlRoot is null
                || !RunPanel.TryBeginTrustPrompt(controller, run.RunId, run.SessionId, DateTimeOffset.UtcNow))
            {
                return;
            }

            _sessionHistoryPresenter.SetActionPending(true);
            UpdateAgentInlineState();

            Exception? failure = null;
            bool dismissed = false;
            try
            {
                // A scratch workspace is a folder Ru created under its own data root for a chat
                // with no chosen project. Asking the user to trust Ru's own scratch space adds
                // noise, so approve it silently; the return still runs the finally that releases
                // the prompt guard. A failure falls through to the normal dialog.
                if (string.Equals(run.WorkspaceKind, "scratch", StringComparison.Ordinal))
                {
                    try
                    {
                        await controller.TrustAsync(approve: true);
                        _workspaceTrustMemory.Remember(_agentWorkspace, run.DisplayIdentityHash);
                        Composer.ForgetAcceptedSend(run.SessionId);
                        return;
                    }
                    catch (Exception exception)
                    {
                        GuiDiagnosticWriter.AppendTraceLine(
                            $"[ChatView] scratch trust approval failed: {exception.GetType().Name}");
                    }
                }

                var prompt = WorkspaceTrustPromptPolicy.For(
                    _workspaceTrustMemory.IsReTrust(_agentWorkspace, run.DisplayIdentityHash),
                    ResolveRunWorkspacePath(run.SessionId));
                bool? decision = await AskWorkspaceTrustAsync(prompt);
                bool isCurrentRequest = ReferenceEquals(_agentController, controller)
                    && RunPanel.OwnsTrustPrompt(controller, run.RunId, run.SessionId)
                    && controller.CurrentRun is { } currentRun
                    && string.Equals(currentRun.RunId, run.RunId, StringComparison.Ordinal)
                    && string.Equals(currentRun.SessionId, _activeSessionId, StringComparison.Ordinal)
                    && controller.NeedsWorkspaceTrust;
                if (decision is null)
                {
                    // Dismissed, not answered: no decision goes to the Host and the run stays
                    // parked, so the prompt can come back when the session is shown again.
                    dismissed = true;
                    return;
                }

                if (!isCurrentRequest)
                {
                    return;
                }

                if (decision == true)
                {
                    await controller.TrustAsync(approve: true);
                    // Only an accepted approval reaches this line: the client throws on a
                    // rejected decision, so the memory cannot claim a trust the Host never recorded.
                    _workspaceTrustMemory.Remember(_agentWorkspace, run.DisplayIdentityHash);
                    Composer.ForgetAcceptedSend(run.SessionId);
                }
                else
                {
                    await controller.TrustAsync(approve: false);
                    // The run ends without starting; the message it carried goes back to
                    // the composer instead of vanishing (B7).
                    if (string.Equals(run.SessionId, _activeSessionId, StringComparison.Ordinal)
                        && Composer.RestoreDeclinedSend(run.SessionId, ChatInputTextBox?.Text ?? string.Empty))
                    {
                        ShowAgentInlineStatus(DeclinedTrustStatus);
                    }
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                // A card taken away without an answer is retried by the next monitor refresh
                // after a short cooldown, not counted as answered.
                bool couldNotOpen = dismissed;
                bool ownsGuard = RunPanel.EndTrustPrompt(controller, run.RunId, run.SessionId, couldNotOpen, DateTimeOffset.UtcNow);
                if (!RunPanel.IsTrustPromptOpen)
                {
                    _sessionHistoryPresenter.SetActionPending(false);
                    UpdateAgentInlineState();
                }

                if (failure is not null && ownsGuard)
                {
                    ShowAgentError(failure);
                }
            }
        }
    }
}
