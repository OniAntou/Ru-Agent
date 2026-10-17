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
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol.Harness;
using RuApp.Controls;
using RuApp.Models;
using RuApp.Services;
using Windows.Storage.Streams;
using System.IO;

namespace RuApp.Views
{
    public sealed partial class ChatView
    {
        private long _agentTurnGeneration;

        private void ShowAgentInlineStatus(string status)
        {
            _localComposerStatus = status;
            ApplyAgentStatusText();
        }

        /// <summary>
        /// Drops local feedback so only actionable run status can remain.
        /// Without this a transient message ("Submitting approval…") outlives
        /// the moment it described and masks later actionable feedback.
        /// </summary>
        private void ClearComposerStatus()
        {
            _localComposerStatus = string.Empty;
            UpdateAgentInlineState();
        }

        /// <summary>
        /// A composer-local message (a rejected attachment, a picker failure) has no
        /// run behind it, so AgentInlinePanelPolicy reports the panel hidden and
        /// <see cref="UpdateAgentInlineState"/> would erase whatever was just written.
        /// Holding the text here and re-projecting it is what keeps a rejected file
        /// from being rejected silently. It is cleared by the next composer action
        /// that replaces the message, or by a turn that starts.
        /// </summary>
        private string _localComposerStatus = string.Empty;

        private void ApplyAgentStatusText()
        {
            string text = _localComposerStatus;
            AgentStatusText.Text = text;
            AgentStatusText.Visibility = string.IsNullOrWhiteSpace(text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        // The markup binds its initial send face to these, so the XAML and the runtime
        // assignment below read the same vocabulary instead of holding two copies.
        public string SendFaceGlyph => AgentComposerPresentation.SendGlyph;

        public string SendFaceName => AgentComposerPresentation.SendName;

        private void UpdateAgentComposerVisuals()
        {
            if (AgentModeLabel is null)
            {
                return;
            }

            AgentModeLabel.Text = AgentComposerPresentation.AgentModeLabelText;
            AgentModeIcon.Glyph = AgentComposerPresentation.AgentModeGlyph;
            AgentModeButton.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(
                AgentModeButton,
                AgentComposerPresentation.AgentModeName);
        }

        public AgentPermissionPreset CurrentAgentPreset => _agentPreset;

        public void SetAgentPreset(AgentPermissionPreset preset)
        {
            _agentPreset = preset;
            UpdateAgentInlineState();
        }

        private bool _isSendButtonInStopMode;
        private string? _workspaceTrustDialogRunId;
        private string? _workspaceTrustDialogSessionId;
        private string? _workspaceTrustDialogHandledRunId;
        private string? _workspaceTrustDialogHandledSessionId;
        private AgentRunController? _workspaceTrustDialogController;

        /// <summary>
        /// Earliest time the trust prompt may be attempted again after a dialog
        /// failure. The failure path itself refreshes the inline state, which
        /// would otherwise re-enter the prompt immediately; the cooldown leaves
        /// the retry to the monitor's next refresh instead of looping.
        /// </summary>
        private DateTimeOffset _workspaceTrustPromptRetryAfter = DateTimeOffset.MinValue;

        private void UpdateSendButtonMode(bool isAgentRunning)
        {
            if (SendButton is null) return;

            bool faceChanged = _isSendButtonInStopMode != isAgentRunning;
            _isSendButtonInStopMode = isAgentRunning;

            void ApplyFace()
            {
                if (SendButtonIcon is null)
                {
                    return;
                }

                SendButtonIcon.Glyph = isAgentRunning
                    ? AgentComposerPresentation.StopGlyph
                    : AgentComposerPresentation.SendGlyph;
                SendButtonIcon.Margin = isAgentRunning ? new Thickness(0) : new Thickness(-1, 1, 0, 0);
                if (Application.Current.Resources.TryGetValue("RuOnAccentBrush", out var onAccent) && onAccent is Brush onAccentBrush)
                {
                    SendButtonIcon.Foreground = onAccentBrush;
                }
            }

            AutomationProperties.SetName(
                SendButton,
                isAgentRunning ? AgentComposerPresentation.StopName : AgentComposerPresentation.SendName);

            // The two faces occupy the same box, so a cross-fade reads as one button
            // turning over instead of a glyph blinking into a different one. Repeat
            // calls with the same face re-apply instantly and never animate.
            if (faceChanged && SendButtonIcon is not null)
            {
                Services.UiMotion.SwapContent(SendButtonIcon, ApplyFace);
            }
            else
            {
                ApplyFace();
            }

            // The composer says which of the two things Enter will do, so a second
            // thought is never typed into a box that silently swallows it.
            if (ChatInputTextBox is not null)
            {
                ChatInputTextBox.PlaceholderText = isAgentRunning
                    ? AgentComposerPresentation.QueuePlaceholder
                    : AgentComposerPresentation.MessagePlaceholder;
            }

            UpdateQueuedTurnsPanel();
        }

        private void ResetWorkspaceTrustDialogIfRunChanged(AgentRunController? controller)
        {
            bool hasInFlightGuard = _workspaceTrustDialogRunId is not null;
            bool hasHandledRun = _workspaceTrustDialogHandledRunId is not null;
            if (!hasInFlightGuard && !hasHandledRun)
            {
                return;
            }

            var run = controller?.CurrentRun;
            bool sameController = ReferenceEquals(_workspaceTrustDialogController, controller);
            bool sameRun = run is not null
                && ((hasInFlightGuard
                        && string.Equals(run.RunId, _workspaceTrustDialogRunId, StringComparison.Ordinal))
                    || (hasHandledRun
                        && string.Equals(run.RunId, _workspaceTrustDialogHandledRunId, StringComparison.Ordinal)));
            bool sameSession = run is not null
                && string.Equals(run.SessionId, _activeSessionId, StringComparison.Ordinal)
                && ((hasInFlightGuard
                        && string.Equals(run.SessionId, _workspaceTrustDialogSessionId, StringComparison.Ordinal))
                    || (hasHandledRun
                        && string.Equals(run.SessionId, _workspaceTrustDialogHandledSessionId, StringComparison.Ordinal)));

            if (!sameController || !sameRun || !sameSession)
            {
                _workspaceTrustDialogRunId = null;
                _workspaceTrustDialogSessionId = null;
                _workspaceTrustDialogHandledRunId = null;
                _workspaceTrustDialogHandledSessionId = null;
                _workspaceTrustDialogController = null;
                _workspaceTrustPromptRetryAfter = DateTimeOffset.MinValue;
            }
        }

        private void UpdateAgentInlineState()
        {
            ResetWorkspaceTrustDialogIfRunChanged(_agentController);
            bool agentBusy = AgentRunActivityPolicy.Evaluate(SnapshotRunActivity()).IsSessionBusy;
            if (_agentBusySeen && !agentBusy)
            {
                SettleAgentActivitiesAfterRun();
            }

            _agentBusySeen = agentBusy;

            // The rules live in AgentInlinePanelPolicy; this method only projects the
            // controller and assigns the result to controls.
            var controller = _agentController;
            var hasController = controller is not null;
            bool runBelongsToAnotherSession = controller?.CurrentRun is { } currentRun
                && !string.Equals(currentRun.SessionId, _activeSessionId, StringComparison.Ordinal);
            bool keepTerminalFailureVisible = hasController
                && AgentFailurePresentation.ShouldKeepVisible(
                    controller!.CurrentRun?.Status,
                    ResolveFailureReason(controller));
            bool isRunningForStatus = AgentRunActivityPolicy.Evaluate(SnapshotRunActivity()).ShowsStopAffordance;
            string statusText = hasController
                ? AgentFailurePresentation.InlineFailureStatus(
                    controller!.CurrentRun?.Status,
                    ResolveFailureReason(controller),
                    isRunningForStatus,
                    HasRenderedAgentReply())
                : string.Empty;

            var state = AgentInlinePanelPolicy.Evaluate(new AgentInlinePanelInput(
                HasController: hasController,
                RunBelongsToAnotherSession: runBelongsToAnotherSession,
                IsBusy: controller?.IsBusy == true,
                CanCancel: controller?.CanCancel == true,
                HasPendingApproval: controller?.HasPendingApproval == true,
                HasPendingInput: controller?.HasPendingInput == true,
                NeedsWorkspaceTrust: controller?.NeedsWorkspaceTrust == true,
                CanContinue: controller?.CanContinue == true,
                KeepTerminalFailureVisible: keepTerminalFailureVisible,
                RunPending: IsActiveAgentRunPending(),
                StatusText: statusText,
                InteractionPending: _agentActionPending));

            UpdateSendButtonMode(isAgentRunning: state.IsRunning);
            UpdateSidePanel(state.IsRunning);

            if (state.ShowWorkspaceTrust)
            {
                ShowWorkspaceTrustDialogAsync();
            }

            Services.UiMotion.SetVisible(
                AgentInlinePanel,
                state.ShowInlinePanel || _localComposerStatus.Length > 0);

            // A composer-local message outlives the refresh that would otherwise
            // blank it: the panel policy only knows about runs.
            if (_localComposerStatus.Length > 0)
            {
                ApplyAgentStatusText();
            }
            else
            {
                AgentStatusText.Text = state.ShowInlinePanel ? state.StatusText : string.Empty;
                AgentStatusText.Visibility = string.IsNullOrWhiteSpace(AgentStatusText.Text)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
            AgentStopButton.Visibility = state.ShowStop ? Visibility.Visible : Visibility.Collapsed;

            if (state.ShowApproval && controller is not null)
            {
                string summary = AgentApprovalSummaryPresentation.Render(
                    controller.PendingApprovalSummary);
                string? terminalCommand = AgentApprovalPresentation.ResolveTerminalCommand(
                    controller.PendingApprovalCommand,
                    AgentActivities);

                ApprovalPanel.Show(
                    summary,
                    terminalCommand,
                    AgentApprovalPresentation.CanAllowSessionDirectory(controller.PendingApprovalSummary),
                    AgentApprovalPresentation.CanRememberAllowAlways(controller.PendingApprovalSummary),
                    AgentApprovalPresentation.CanRememberForSession(controller.PendingApprovalSummary));

                // Approval arrives via SSE while the user may be scrolled up; bring
                // the Approve/Deny prompt into view and put the keyboard on it. The
                // focus move happens once per new approval only: this projection
                // reruns on every refresh while the panel is open, and re-grabbing
                // the keyboard would make the rest of the panel unusable.
                ScrollChatToBottom();
                if (!string.Equals(_focusedApprovalId, controller.PendingApprovalId, StringComparison.Ordinal))
                {
                    _focusedApprovalId = controller.PendingApprovalId;
                    ApprovalPanel.FocusDecision();
                }
            }
            else
            {
                _focusedApprovalId = null;
                ApprovalPanel.Hide();
            }

            if (state.ShowQuestion && controller is not null)
            {
                QuestionPanel.Show(
                    controller.PendingInputId ?? string.Empty,
                    AgentQuestionPresentation.RenderQuestion(controller.PendingInputQuestion),
                    AgentQuestionPresentation.RenderOptions(controller.PendingInputOptions));

                // The question arrives via SSE while the user may be scrolled up;
                // bring the card into view and put the keyboard in the answer box.
                // The focus move happens once per new question only, for the same
                // reason the approval prompt tracks its id.
                ScrollChatToBottom();
                if (!string.Equals(_focusedInputId, controller.PendingInputId, StringComparison.Ordinal))
                {
                    _focusedInputId = controller.PendingInputId;
                    QuestionPanel.FocusAnswer();
                }
            }
            else
            {
                _focusedInputId = null;
                QuestionPanel.Hide();
            }

            ApprovalPanel.SetDecisionEnabled(!state.SuppressInteraction);
            QuestionPanel.SetSubmitEnabled(!state.SuppressInteraction);
            if (AgentStopButton is not null)
            {
                AgentStopButton.IsEnabled = !state.SuppressInteraction;
            }
        }

        private async void ShowWorkspaceTrustDialogAsync()
        {
            var controller = _agentController;
            var run = controller?.CurrentRun;
            if (_workspaceTrustDialogRunId is not null)
            {
                return;
            }

            if (controller is null
                || run is null
                || !string.Equals(run.SessionId, _activeSessionId, StringComparison.Ordinal)
                || !controller.NeedsWorkspaceTrust
                || XamlRoot is null)
            {
                return;
            }

            if (DateTimeOffset.UtcNow < _workspaceTrustPromptRetryAfter)
            {
                // A failed prompt set a short cooldown: the refresh that follows
                // the failure must not re-enter the prompt immediately. The
                // monitor's next refresh retries once the cooldown elapses.
                return;
            }

            if (_workspaceTrustDialogHandledRunId is not null
                && ReferenceEquals(_workspaceTrustDialogController, controller)
                && string.Equals(_workspaceTrustDialogHandledRunId, run.RunId, StringComparison.Ordinal)
                && string.Equals(_workspaceTrustDialogHandledSessionId, run.SessionId, StringComparison.Ordinal))
            {
                return;
            }

            _workspaceTrustPromptRetryAfter = DateTimeOffset.MinValue;
            _workspaceTrustDialogRunId = run.RunId;
            _workspaceTrustDialogSessionId = run.SessionId;
            _workspaceTrustDialogController = controller;
            _agentActionPending = true;
            UpdateAgentInlineState();

            Exception? failure = null;
            try
            {
                var prompt = WorkspaceTrustPromptPolicy.For(
                    _workspaceTrustMemory.IsReTrust(_agentWorkspace, run.DisplayIdentityHash));
                var dialog = new ContentDialog
                {
                    Title = prompt.Title,
                    Content = prompt.Content,
                    PrimaryButtonText = prompt.PrimaryButtonText,
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                };

                ContentDialogResult result = await ContentDialogGate.ShowAsync(dialog);
                bool isCurrentRequest = ReferenceEquals(_agentController, controller)
                    && string.Equals(_workspaceTrustDialogRunId, run.RunId, StringComparison.Ordinal)
                    && controller.CurrentRun is { } currentRun
                    && string.Equals(currentRun.RunId, run.RunId, StringComparison.Ordinal)
                    && string.Equals(currentRun.SessionId, _activeSessionId, StringComparison.Ordinal)
                    && controller.NeedsWorkspaceTrust;
                if (!isCurrentRequest)
                {
                    return;
                }

                if (result == ContentDialogResult.Primary)
                {
                    await controller.TrustAsync(approve: true);
                    // Only an accepted approval reaches this line: the client throws
                    // on a rejected decision, so the memory cannot claim a trust the
                    // Host never recorded.
                    _workspaceTrustMemory.Remember(_agentWorkspace, run.DisplayIdentityHash);
                }
                else
                {
                    await controller.TrustAsync(approve: false);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                bool ownsGuard = ReferenceEquals(_workspaceTrustDialogController, controller)
                    && string.Equals(_workspaceTrustDialogRunId, run.RunId, StringComparison.Ordinal)
                    && string.Equals(_workspaceTrustDialogSessionId, run.SessionId, StringComparison.Ordinal);
                bool retryable = failure is not null && ContentDialogGate.IsDialogConflict(failure);
                if (retryable)
                {
                    // Set before the state refresh below, so the refresh that this
                    // failure triggers cannot re-enter the prompt immediately.
                    _workspaceTrustPromptRetryAfter = DateTimeOffset.UtcNow.AddSeconds(1);
                }

                if (ownsGuard)
                {
                    if (!retryable)
                    {
                        // Only an answered prompt (or a non-dialog failure) counts
                        // as handled. A prompt that never opened must be offered
                        // again: the run is parked on trust, and the monitor
                        // refresh retries within its interval.
                        _workspaceTrustDialogHandledRunId = run.RunId;
                        _workspaceTrustDialogHandledSessionId = run.SessionId;
                    }

                    _workspaceTrustDialogRunId = null;
                    _workspaceTrustDialogSessionId = null;
                }

                if (_workspaceTrustDialogRunId is null)
                {
                    _agentActionPending = false;
                    UpdateAgentInlineState();
                }

                if (failure is not null && ownsGuard)
                {
                    if (retryable)
                    {
                        // A prompt that could not open is not a start failure:
                        // say what happened and let the next refresh retry it.
                        ShowAgentInlineStatus("Could not open the workspace trust prompt. Close any open dialog; the prompt will reappear.");
                    }
                    else
                    {
                        ShowAgentError(failure);
                    }
                }
            }
        }

        private static string ResolveFailureReason(AgentRunController controller)
        {
            // The SSE final_snapshot only carries the bare run status ("failed"),
            // which tells the user nothing. Prefer the durable terminal error
            // code (repeated_tool_call, provider_rate_limited, ...) whenever the
            // stream text is just a bare status word.
            string streamText = controller.StatusText ?? string.Empty;
            string? terminalCode = string.Equals(controller.CurrentRun?.Status, "failed", StringComparison.Ordinal)
                ? controller.CurrentRun?.TerminalErrorCode
                : null;
            if (!string.IsNullOrWhiteSpace(terminalCode) && IsBareRunStatusWord(streamText))
            {
                return terminalCode!;
            }

            return streamText;
        }

        private static bool IsBareRunStatusWord(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            return text.Trim() is string trimmed
                && (string.Equals(trimmed, "failed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed, "completed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed, "cancelled", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed, "interrupted", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed, "Run ended.", StringComparison.OrdinalIgnoreCase));
        }

        // ── Run activity ───────────────────────────────────────
        // "Is a run in hand for this session?" is one question with one answer, asked
        // by the send guard, the delta gate, the tool-output gate, the inline panel,
        // and the composer mode. It lives in AgentRunActivityPolicy; the view only
        // projects its own state and the controller's into it.
        private AgentRunActivityInput SnapshotRunActivity()
        {
            string? runSessionId = _agentController?.CurrentRun?.SessionId;
            return new AgentRunActivityInput(
                IsSendingTurn: _isSendingUserTurn,
                HasRun: _agentController?.CurrentRun is not null,
                RunSessionId: runSessionId,
                ActiveSessionId: _activeSessionId,
                CanCancel: _agentController?.CanCancel == true,
                HasPendingApproval: _agentController?.HasPendingApproval == true,
                HasPendingInput: _agentController?.HasPendingInput == true,
                NeedsWorkspaceTrust: _agentController?.NeedsWorkspaceTrust == true,
                IsBusy: _agentController?.IsBusy == true);
        }

        /// <summary>
        /// True while this session has a run in hand, including the send window. The
        /// synchronous send guard lives here: entry points (Enter key, Send click) use
        /// this, so a second Enter during the create-run await cannot produce a
        /// duplicate user bubble and a duplicate run.
        /// </summary>
        private bool IsActiveAgentRunPending() =>
            AgentRunActivityPolicy.Evaluate(SnapshotRunActivity()).IsGuarded;

        /// <summary>
        /// Whether a live stream frame belongs in this chat: the controller is busy on
        /// this session, or the view is between tapping Send and the run existing. Every
        /// live-stream gate asks this one question.
        /// </summary>
        private bool HasRunInHand() =>
            AgentRunActivityPolicy.Evaluate(SnapshotRunActivity()) is { IsSessionBusy: true }
                or { Reason: AgentRunActivityReason.Sending };

        /// <summary>
        /// The controller's own view of the run, without the send guard.
        /// SendAgentTurnAsync must use this: it already holds the guard, so asking the
        /// guarded question there would block its own caller.
        /// </summary>
        private bool IsControllerRunPending() =>
            AgentRunActivityPolicy.IsControllerRunPending(SnapshotRunActivity());

        private void ResetAgentState(bool preserveTranscript = false)
        {
            _sidePanelTasksRunId = null;
            _agentTurnGeneration++;
            if (preserveTranscript)
                SuspendAgentFeedUpdates();
            else
                ClearAgentActivities();
            ClearTodoPanel();
            // The queue belongs to the session that was on screen; a queue that
            // followed the user into another conversation would send a turn into the
            // wrong transcript.
            _queuedTurns.Clear();

            _sessionPresentation.StopMonitoring();
            _agentActionPending = false;
            _workspaceTrustDialogRunId = null;
            _workspaceTrustDialogSessionId = null;
            _workspaceTrustDialogHandledRunId = null;
            _workspaceTrustDialogHandledSessionId = null;
            _workspaceTrustDialogController = null;
            _isSendingUserTurn = false;
            _activeAgentUsage = null;
            // The preset is deliberately not reset here. It is session-scoped
            // and owned by the user: a Full Access session reopened in the GUI
            // came back Standard, and a Standard one came back Plan, both
            // without a word on screen. The default is already in the field
            // initialiser and the XAML SelectedIndex; these lines only ever
            // narrowed what the user had chosen.
            _agentPresetSessionId = null;
            _agentPresetVersion = null;
            AbandonActiveAgentReply(preservePresentation: preserveTranscript);

            UpdateAgentComposerVisuals();
            Services.UiMotion.SetVisible(AgentInlinePanel, false);
            ApprovalPanel.Hide();
            QuestionPanel.Hide();
            AgentStopButton.Visibility = Visibility.Collapsed;
            _localComposerStatus = string.Empty;
            ApplyAgentStatusText();
            UpdateSendButtonMode(isAgentRunning: false);
        }


        private static string StripDisplayMarkers(string content) => ChatDisplayMarkers.Strip(content);

        private async void OnSendClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isSendButtonInStopMode || IsActiveAgentRunPending())
                {
                    await StopActiveAgentAsync();
                    return;
                }

                await SendCurrentMessageAsync();
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.WriteCrashEvent(ex);
                ShowAgentError(ex);
            }
        }



        private void OnRemoveAttachmentClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var item = PendingAttachments.FirstOrDefault(a => a.Id == id);
                if (item != null)
                {
                    PendingAttachments.Remove(item);
                    if (!_composerDrafts.TrySave(_activeSessionId, ChatInputTextBox.Text, PendingAttachments))
                    {
                        ShowAgentInlineStatus(ComposerDraftStore.RetentionLimitStatus);
                    }
                    else if (_localComposerStatus == ComposerDraftStore.RetentionLimitStatus)
                    {
                        ClearComposerStatus();
                    }
                }
                if (PendingAttachments.Count == 0)
                {
                    AttachmentPreviewContainer.Visibility = Visibility.Collapsed;
                }
            }
        }
        private async void OnChatInputPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            try
            {
                if (e.Key == Windows.System.VirtualKey.V)
                {
                    var ctrlDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                    if (ctrlDown)
                    {
                        var package = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                        if (package.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap)
                            || package.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                        {
                            e.Handled = true;
                            await TryPasteImageFromClipboardAsync();
                            return;
                        }
                    }
                }

                if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    // Escape is the one key that belongs to the app rather than to the
                    // text field, so it is the only way out of a run that needs no mouse.
                    // The decision is not made here: an open approval outranks the run, and
                    // that precedence lives in one policy so it can be tested.
                    var escape = AgentRunKeyboardPolicy.Resolve(
                        runInHand: _isSendButtonInStopMode || IsActiveAgentRunPending(),
                        approvalShowing: ApprovalPanel?.IsShown == true,
                        questionShowing: QuestionPanel?.IsShown == true);

                    switch (escape)
                    {
                        case EscapeOutcome.DenyApproval:
                            e.Handled = true;
                            break;
                        // An approval showing with focus in the composer: the panel cannot
                        // see the key, so it is swallowed here rather than falling through
                        // to stop the run. Denying needs the panel's own handler.
                        case EscapeOutcome.StopRun:
                            e.Handled = true;
                            await StopActiveAgentAsync();
                            return;
                        default:
                            // No run, no question: the field keeps Escape, which is what a
                            // single-line box is for.
                            break;
                    }
                }

                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    var shiftDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                    if (!shiftDown)
                    {
                        e.Handled = true;
                        if (_isSendButtonInStopMode || IsActiveAgentRunPending())
                        {
                            // A second thought lands in the queue instead of being
                            // dropped: the run in hand owns the turn boundary, and
                            // Enter still means "this is what I want next".
                            QueueCurrentComposerTurn();
                            return;
                        }
                        await SendCurrentMessageAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"Error in OnChatInputPreviewKeyDown: {ex.Message}");
            }
        }

        /// <summary>
        /// Escape anywhere in the chat surface that the composer did not already answer.
        /// </summary>
        /// <remarks>
        /// The composer is a Preview handler, so it sees the key first and marks it
        /// handled; this one only fires when focus is on the transcript, a panel or
        /// anywhere else in the surface. Both resolve the same policy, so Escape means
        /// the same thing whichever of them receives it.
        ///
        /// An open approval is deliberately not answered from here. The panel's own
        /// handler owns that decision, and this handler only sees the case where the
        /// panel is on screen but focus is elsewhere; swallowing the key is the safe
        /// outcome, because the alternative would be stopping the run over a question the
        /// user has not been asked yet.
        /// </remarks>
        private async void OnChatSurfaceKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Escape)
            {
                return;
            }

            var escape = AgentRunKeyboardPolicy.Resolve(
                runInHand: _isSendButtonInStopMode || IsActiveAgentRunPending(),
                approvalShowing: ApprovalPanel?.IsShown == true,
                questionShowing: QuestionPanel?.IsShown == true);

            if (escape == EscapeOutcome.DenyApproval)
            {
                e.Handled = true;
                return;
            }

            if (escape != EscapeOutcome.StopRun)
            {
                return;
            }

            e.Handled = true;
            try
            {
                await StopActiveAgentAsync();
            }
            catch (Exception ex)
            {
                // The composer handler logs its own failures; this one must not throw out
                // of a key event, or the exception surfaces as an unhandled app crash
                // rather than as a run that did not stop.
                GuiDiagnosticWriter.AppendTraceLine($"Escape failed to stop the run: {ex.Message}");
            }
        }

        private void OnInputDragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "Drop to attach files / images";
                e.DragUIOverride.IsContentVisible = true;
            }
        }

        private async Task SendCurrentMessageAsync()
        {
            if (_isSendingUserTurn || _isSessionHistoryLoading)
            {
                return;
            }

            long submittedGeneration = _agentTurnGeneration;
            try
            {
                string text = ChatInputTextBox.Text.Trim();
                string composerText = ChatInputTextBox.Text;
                var attachments = PendingAttachments.ToList();

                if (string.IsNullOrEmpty(text) && attachments.Count == 0) return;

                if (IsContinueResumeCommand(text, attachments) && _agentController?.CanContinue == true)
                {
                    // Typed "Continue" resumes a resumable run instead of
                    // starting a new turn; the failure message points here.
                    ChatInputTextBox.Text = string.Empty;
                    await RunAgentActionAsync(
                        () => _agentController?.ContinueAsync() ?? Task.CompletedTask,
                        "Resuming Coding Agent…");
                    return;
                }

                // The limit is checked before the composer enters its sending face:
                // bailing out after UpdateSendButtonMode(true) leaves the button showing
                // Stop with no run behind it, and the next send starts from a broken state.
                int imageCount = attachments.Count(static attachment => attachment.IsImage);
                if (imageCount > ChatAttachmentPolicy.MaxImageCount)
                {
                    ShowAgentInlineStatus(ChatAttachmentPolicy.TooManyImagesStatus);
                    return;
                }

                _isSendingUserTurn = true;
                UpdateSendButtonMode(isAgentRunning: true);

                await EnsureAgentSessionAsync(CancellationToken.None);
                string submittedSessionId = _activeSessionId;
                bool accepted = await SendAgentTurnAsync(
                    BuildEffectivePrompt(text, attachments),
                    CancellationToken.None,
                    attachments: attachments,
                    displayUserText: text,
                    onAccepted: () =>
                    {
                        if (submittedGeneration != _agentTurnGeneration
                            || !string.Equals(_activeSessionId, submittedSessionId, StringComparison.Ordinal)
                            || !string.Equals(ChatInputTextBox.Text, composerText, StringComparison.Ordinal)
                            || !PendingAttachments.SequenceEqual(attachments))
                        {
                            return;
                        }

                        ChatInputTextBox.Text = string.Empty;
                        PendingAttachments.Clear();
                        AttachmentPreviewContainer.Visibility = Visibility.Collapsed;
                        ClearComposerDraft(submittedSessionId);
                    });
                if (!accepted && submittedGeneration == _agentTurnGeneration)
                {
                    UpdateSendButtonMode(isAgentRunning: false);
                }
            }
            catch (Exception ex)
            {
                if (submittedGeneration == _agentTurnGeneration)
                {
                    FailActiveAgentReply(ex);
                    ShowAgentError(ex);
                }
            }
            finally
            {
                if (submittedGeneration == _agentTurnGeneration)
                {
                    _isSendingUserTurn = false;
                    UpdateAgentInlineState();
                }
            }
        }

        /// <summary>
        /// The prompt the Agent actually receives: the typed text with every attached
        /// document inlined above it as labelled fenced blocks. The send path and the
        /// queue drain share it, so a turn is inlined the same way however it left
        /// the composer.
        /// </summary>
        private static string BuildEffectivePrompt(string text, IReadOnlyList<ImageAttachmentItem> attachments)
        {
            var textAttachments = attachments
                .Where(attachment => !attachment.IsImage && !string.IsNullOrWhiteSpace(attachment.TextContent))
                .ToList();
            if (textAttachments.Count == 0)
            {
                return text;
            }

            var builder = new System.Text.StringBuilder();
            foreach (var document in textAttachments)
            {
                string language = document.FileExtension.TrimStart('.');
                builder.AppendLine($"[Attached file: {document.FileName} ({document.FormattedSize})]");
                builder.AppendLine($"```{language}");
                builder.AppendLine(document.TextContent);
                builder.AppendLine("```");
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                builder.AppendLine(text);
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// Takes what the composer holds and puts it behind the run in hand. False
        /// means the text stayed where it was: nothing to queue, or the queue is full.
        /// </summary>
        private bool QueueCurrentComposerTurn()
        {
            string text = ChatInputTextBox?.Text?.Trim() ?? string.Empty;
            var attachments = PendingAttachments.ToList();
            if (text.Length == 0 && attachments.Count == 0)
            {
                return false;
            }

            if (!_queuedTurns.TryEnqueue(text, text, attachments))
            {
                ShowAgentInlineStatus(AgentComposerPresentation.QueueFullStatus);
                return false;
            }

            if (ChatInputTextBox is not null)
            {
                ChatInputTextBox.Text = string.Empty;
            }
            PendingAttachments.Clear();
            AttachmentPreviewContainer.Visibility = Visibility.Collapsed;
            ClearComposerDraft(_activeSessionId);
            ShowAgentInlineStatus(AgentComposerPresentation.QueuedStatus);
            return true;
        }

        /// <summary>
        /// Shows a row's fold chevron while the pointer is on it, or while it holds focus.
        /// </summary>
        /// <remarks>
        /// The chevron says "this line folds", which a reader learns once; after that it is a
        /// column of glyphs down the whole feed. It is collapsed rather than dimmed — a muted
        /// glyph at low opacity is the double dimming the palette rules exist to prevent — and
        /// it sits in a fixed-width box so the label beside it does not jump sideways the
        /// moment the pointer arrives. Focus counts as hovering, because a keyboard reader has
        /// no pointer to reveal it with.
        /// </remarks>
        private void OnActivityTogglePointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
            SetActivityChevronVisible(sender, true);

        private void OnActivityTogglePointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
            SetActivityChevronVisible(sender, false);

        private void OnActivityToggleGotFocus(object sender, RoutedEventArgs e) =>
            SetActivityChevronVisible(sender, true);

        private void OnActivityToggleLostFocus(object sender, RoutedEventArgs e) =>
            SetActivityChevronVisible(sender, false);

        private static void SetActivityChevronVisible(object sender, bool visible)
        {
            if (sender is not ToggleButton toggle || toggle.Content is not DependencyObject content)
            {
                return;
            }

            // One glyph per toggle header, and it is the only icon in there.
            FontIcon? chevron = EnumerateDescendants(content).OfType<FontIcon>().FirstOrDefault();
            if (chevron is not null)
            {
                chevron.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void OnRemoveQueuedTurnClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is QueuedAgentTurn turn)
            {
                _queuedTurns.Remove(turn);
            }
        }

        private async void OnSendQueuedTurnsClick(object sender, RoutedEventArgs e)
        {
            try
            {
                // A hand-driven flush answers this click, so the stop hold that was
                // guarding the automatic drain no longer applies to what is left.
                _queuedTurns.ReleaseHold();
                await DrainQueuedTurnsAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                ShowAgentError(exception);
            }
        }

        /// <summary>
        /// Hands the first queued turn over: called when a turn reaches its end, and
        /// by the strip's own Send now control.
        /// </summary>
        private async Task DrainQueuedTurnsAsync(CancellationToken cancellationToken)
        {
            if (_agentController is null) return;
            var sessionId = _activeSessionId;
            var generation = _agentTurnGeneration;
            try
            {
                await _queuedTurns.DrainAsync((next, accepted) => SendAgentTurnAsync(
                    BuildEffectivePrompt(next.Text, next.Attachments), cancellationToken,
                    attachments: next.Attachments.ToList(), displayUserText: next.DisplayText,
                    onAccepted: accepted));
            }
            catch (Exception exception)
            {
                if (generation == _agentTurnGeneration && sessionId == _activeSessionId)
                {
                    FailActiveAgentReply(exception);
                    ShowAgentError(exception);
                }
            }
            finally { UpdateQueuedTurnsPanel(); }
        }

        private void UpdateQueuedTurnsPanel()
        {
            if (QueuedTurnsPanel is null || QueuedTurnsHeader is null || QueuedTurnsSendButton is null)
            {
                return;
            }

            int count = _queuedTurns.Items.Count;
            Services.UiMotion.SetVisible(QueuedTurnsPanel, count > 0);
            QueuedTurnsHeader.Text = count == 1 ? "1 message queued" : $"{count} messages queued";

            // While a run is in hand the queue drains on its own, so offering to send
            // it by hand would promise a turn boundary the user cannot have yet.
            bool canSendNow = count > 0 && !_isSendButtonInStopMode && !IsActiveAgentRunPending();
            QueuedTurnsSendButton.Visibility = canSendNow ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Sends one turn. Returns false when no run was started (a run is still in
        /// hand, or the caller declined Full Access), so a queued turn can be put
        /// back instead of being lost.
        /// </summary>
        private async Task<bool> SendAgentTurnAsync(
            string text,
            CancellationToken cancellationToken,
            bool allowSessionRecovery = true,
            List<ImageAttachmentItem>? attachments = null,
            string? displayUserText = null,
            bool addUserMessage = true,
            Action? onAccepted = null)
        {
            if (allowSessionRecovery)
            {
                _agentSessionRecovery.Reset();
            }

            if (_agentController is null)
            {
                throw new InvalidOperationException("Agent controller is unavailable.");
            }

            if (_rewindInProgress)
            {
                ShowAgentInlineStatus("Wait for the history rewind to finish before sending.");
                return false;
            }

            if (IsControllerRunPending())
            {
                ShowAgentInlineStatus("Coding Agent is already running. Please complete or stop the current turn first.");
                return false;
            }


            await EnsureAgentSessionAsync(cancellationToken);
            string workspace = AgentWorkspaceSelection.Resolve(_requestedWorkspace, _defaultAgentWorkspace)
                ?? _defaultAgentWorkspace
                ?? throw new InvalidOperationException("App root workspace is not configured.");

            _agentWorkspace = workspace;

            string? submittedSessionId = null;
            try
            {
                AgentPermissionPreset preset = _agentPreset;
                if (preset == AgentPermissionPreset.FullAccess && !await ConfirmFullAccessAsync())
                {
                    _agentPreset = AgentPermissionPreset.Standard;
                    UpdateAgentInlineState();
                    return false;
                }

                _agentPreset = preset;
                long presetVersion = await EnsureAgentPresetAsync(preset, cancellationToken);
                // The preset choice is stored server-side per session: the run
                // request carries the same wire value so the stored selection and
                // the run agree, but the stored selection stays authoritative.
                string wirePreset = preset switch
                {
                    AgentPermissionPreset.Plan => "plan",
                    AgentPermissionPreset.FullAccess => "full_access",
                    _ => "standard",
                };
                var imagePayloads = attachments?
                    .Where(a => a.IsImage && (!string.IsNullOrEmpty(a.Base64Data) || !string.IsNullOrEmpty(a.FilePath)))
                    .Select(a => !string.IsNullOrEmpty(a.Base64Data) ? $"data:{a.MimeType};base64,{a.Base64Data}" : a.FilePath!)
                    .ToList();

                submittedSessionId = _activeSessionId;
                var request = new CreateAgentRunRequest(
                    submittedSessionId,
                    workspace,
                    text,
                    wirePreset,
                    presetVersion,
                    Guid.NewGuid().ToString("N"),
                    imagePayloads);

                _activeWorkspaceId = null;

                bool accepted = await _agentController.CreateAndRunAsync(
                    request,
                    cancellationToken,
                    () => BeginAcceptedAgentTurn(
                        submittedSessionId,
                        text,
                        attachments,
                        displayUserText,
                        addUserMessage,
                        onAccepted));
                if (!string.Equals(_activeSessionId, submittedSessionId, StringComparison.Ordinal))
                {
                    return accepted;
                }

                if (!accepted)
                {
                    ShowAgentInlineStatus("Coding Agent is still creating another run. This message and its attachments are still in the composer.");
                    return false;
                }

                _activeWorkspaceId = _agentController.CurrentRun?.WorkspaceId;
            }
            catch (AgentApiException error) when (
                allowSessionRecovery
                && AgentSessionRecovery.IsRecoverable(error)
                && submittedSessionId is not null
                && string.Equals(_activeSessionId, submittedSessionId, StringComparison.Ordinal)
                && _agentSessionRecovery.TryBegin(submittedSessionId))
            {
                // Preserve the completion gate while replacing the stale
                // session and retrying this same logical turn once.
                AbandonActiveAgentReply(removeUserMessage: true);
                await RecoverAgentSessionAsync(cancellationToken);
                return await SendAgentTurnAsync(
                    text,
                    cancellationToken,
                    allowSessionRecovery: false,
                    attachments: attachments,
                    displayUserText: displayUserText,
                    onAccepted: onAccepted);
            }

            if (IsTerminalStatus(_agentController.CurrentRun?.Status))
            {
                await CompleteActiveAgentTurnAsync(cancellationToken);
            }
            else
            {
                StartAgentMonitor();
            }
            UpdateAgentInlineState();
            return true;
        }

        private void BeginAcceptedAgentTurn(
            string sessionId,
            string text,
            List<ImageAttachmentItem>? attachments,
            string? displayUserText,
            bool addUserMessage,
            Action? onAccepted)
        {
            if (!string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            StartNewAgentActivityRun();
            BeginAgentReply(text, attachments, displayUserText, addUserMessage);
            ClearComposerStatus();
            JumpToLatestForNewTurn();
            ScrollChatToBottom();
            AgentStopButton.Visibility = Visibility.Collapsed;
            UpdateSendButtonMode(isAgentRunning: true);
            UpdateAgentInlineState();
            onAccepted?.Invoke();
        }

        private void BeginAgentReply(string text, List<ImageAttachmentItem>? attachments = null, string? displayUserText = null, bool addUserMessage = true)
        {
            _activeAgentRunId = null;
            // CurrentRun can request trust before the accepted-turn callback
            // reaches this method. Only the run/session owner resets its dialog.
            _activeAgentReplyReceived = false;
            _sessionPresentation.BeginTurn();
            _activeAgentUsage = null;
            _replyRenderScheduler.Invalidate();
            lock (_accumulatorLock)
            {
                _replyAccumulator.Begin(_activeSessionId, _activeSessionId);
            }

            if (addUserMessage)
            {
                Messages.Add(new ChatMessageItem
                {
                    Role = "user",
                    Content = !string.IsNullOrEmpty(displayUserText) ? displayUserText : text,
                    SessionId = _activeSessionId,
                    Attachments = attachments != null && attachments.Count > 0
                        ? new ObservableCollection<ImageAttachmentItem>(attachments)
                        : new ObservableCollection<ImageAttachmentItem>()
                });
                if (!string.IsNullOrEmpty(_activeSessionId))
                {
                    SessionStarted?.Invoke(this, new SessionStartedEventArgs(
                        _activeSessionId, !string.IsNullOrEmpty(displayUserText) ? displayUserText : text));
                }
            }

            _activeAgentAssistantMessage = new ChatMessageItem
            {
                Role = "assistant",
                Content = string.Empty,
                IsStreaming = true,
                SessionId = _activeSessionId,
            };
            _activeAgentAssistantMessage.AgentActivities = AgentActivities;
            _segmentFrozenLength = 0;
            Messages.Add(_activeAgentAssistantMessage);
        }

        private async Task CompleteActiveAgentTurnAsync(CancellationToken cancellationToken)
        {
            var completion = _sessionPresentation.TryBeginCompletion();
            if (completion is null) return;
            FlushPendingReplyText();
            // Flush any pending coalesced render before completing
            AgentReplySnapshot? pending = _replyRenderScheduler.Flush();
            if (pending != null && _activeAgentAssistantMessage != null)
            {
                if (!Messages.Contains(_activeAgentAssistantMessage))
                {
                    Messages.Add(_activeAgentAssistantMessage);
                }
                ApplyReplyText(_activeAgentAssistantMessage, pending.Text, pending.ResetOffset);
                _activeAgentAssistantMessage.IsStreaming = false;
                _activeAgentAssistantMessage.SettleSegments();
                UpdateLiveTailText(_activeAgentAssistantMessage);
                ScrollChatToBottom();
            }
            else if (_activeAgentAssistantMessage != null)
            {
                _activeAgentAssistantMessage.IsStreaming = false;
                _activeAgentAssistantMessage.SettleSegments();
            }
            ChatMessageItem? placeholder = _activeAgentAssistantMessage;
            var runId = _activeAgentRunId;

            string? latestAssistant = await ReloadSessionHistoryAsync(cancellationToken);
            if (!_sessionPresentation.IsCurrent(completion.Value)
                || !string.Equals(runId, _activeAgentRunId, StringComparison.Ordinal)
                || !ReferenceEquals(placeholder, _activeAgentAssistantMessage)) return;
            if (latestAssistant is null
                && !_activeAgentReplyReceived
                && placeholder is not null
                && string.IsNullOrWhiteSpace(placeholder.Content)
                && Messages.Contains(placeholder))
            {
                Messages.Remove(placeholder);
            }

            // No run-level failure card is re-appended after a reload. The activity
            // feed is rebuilt from session history, so the settled row that names the
            // failure comes back with everything else; the card used to arrive a
            // second time, below the work it explained.
            _activeAgentAssistantMessage = null;
            _activeAgentRunId = null;
            UpdateAgentInlineState();

            // Queued turns waited for exactly this moment: the previous turn is over
            // and its transcript is settled, so the next one may start.
            await DrainQueuedTurnsAsync(cancellationToken);
        }
        public static bool IsContinueResumeCommand(string text, IReadOnlyCollection<ImageAttachmentItem> attachments)
        {
            ArgumentNullException.ThrowIfNull(text);
            ArgumentNullException.ThrowIfNull(attachments);
            return attachments.Count == 0
                && string.Equals(text.Trim(), "Continue", StringComparison.OrdinalIgnoreCase);
        }

        private static Task<byte[]?> ReadBoundedBytesAsync(System.IO.Stream stream, int maximumBytes) =>
            Services.ChatAttachmentPolicy.ReadBoundedBytesAsync(stream, maximumBytes);

        private static bool IsImageFile(string name) => Services.ChatAttachmentPolicy.IsImageFile(name);

        private static bool IsTextOrCodeFile(string name) => Services.ChatAttachmentPolicy.IsTextOrCodeFile(name);

        private void FailActiveAgentReply(Exception exception)
        {
            FlushPendingReplyText();
            AgentReplySnapshot? pending = _replyRenderScheduler.Flush();
            if (pending is not null && _activeAgentAssistantMessage is not null)
            {
                ApplyReplyText(_activeAgentAssistantMessage, pending.Text, pending.ResetOffset);
            }

            bool hasReply = HasRenderedAgentReply();
            _replyRenderScheduler.Invalidate();
            if (_activeAgentAssistantMessage is not null)
            {
                if (!Messages.Contains(_activeAgentAssistantMessage))
                {
                    Messages.Add(_activeAgentAssistantMessage);
                }
                if (!hasReply)
                {
                    _activeAgentAssistantMessage.Content = "Coding Agent could not complete this turn. Please try again later.";
                    UpdateLiveTailText(_activeAgentAssistantMessage);
                }
                _activeAgentAssistantMessage.IsStreaming = false;
                _activeAgentAssistantMessage.SettleSegments();
                _activeAgentAssistantMessage.IsError = !hasReply;
                SyncSegmentErrorState(_activeAgentAssistantMessage);
            }

            _activeAgentAssistantMessage = null;
            _activeAgentRunId = null;
        }

        private void AbandonActiveAgentReply(bool removeUserMessage = false, bool preservePresentation = false)
        {
            _replyRenderScheduler.Invalidate();

            if (removeUserMessage && Messages.Count > 0)
            {
                var last = Messages[^1];
                if (string.Equals(last.Role, "user", StringComparison.Ordinal))
                {
                    Messages.RemoveAt(Messages.Count - 1);
                }
            }

            if (!preservePresentation && _activeAgentAssistantMessage is not null)
            {
                var abandonedMessage = _activeAgentAssistantMessage;
                abandonedMessage.IsStreaming = false;
                abandonedMessage.SettleSegments();
                if (string.IsNullOrWhiteSpace(abandonedMessage.Content)
                    && Messages.Contains(abandonedMessage))
                {
                    Messages.Remove(abandonedMessage);
                }
            }

            _activeAgentAssistantMessage = null;
            _activeAgentRunId = null;
            _activeAgentReplyReceived = false;
            _sessionPresentation.BeginTurn();
        }

        private async Task<SessionInfo> CreateValidatedAgentSessionAsync(CancellationToken cancellationToken)
        {
            if (_apiService is null)
            {
                throw new InvalidOperationException("Backend session is unavailable.");
            }

            SessionInfo? created = await _apiService.CreateSessionAsync();
            cancellationToken.ThrowIfCancellationRequested();
            string? sessionId = created?.Id?.Trim();
            if (created is null || !AgentSessionSelection.IsValid(sessionId))
            {
                throw new AgentApiException(
                    "session_create_failed",
                    "Could not create a session for Coding Agent.");
            }

            return created;
        }

        private async Task RecoverAgentSessionAsync(CancellationToken cancellationToken)
        {
            SessionInfo created = await CreateValidatedAgentSessionAsync(cancellationToken);
            string sessionId = created.Id.Trim();

            ActivateSession(sessionId);
            _agentPresetSessionId = null;
            _agentPresetVersion = null;
            ClearAgentActivities();

            DetachAgentActivitiesFromMessages();
            Messages.Clear();
            UpdateViewMode();
            SessionOpened?.Invoke(
                this,
                new SessionOpenedEventArgs(
                    sessionId,
                    created.Title ?? "New Conversation"));
        }

        private async Task EnsureAgentSessionAsync(CancellationToken cancellationToken)
        {
            if (AgentSessionSelection.IsValid(_activeSessionId))
            {
                return;
            }

            SessionInfo created = await CreateValidatedAgentSessionAsync(cancellationToken);
            string validSessionId = created.Id.Trim();
            ActivateSession(validSessionId);
            SessionOpened?.Invoke(
                this,
                new SessionOpenedEventArgs(
                    validSessionId,
                    created.Title ?? "New Conversation"));
        }

        private async Task<long> EnsureAgentPresetAsync(
            AgentPermissionPreset preset,
            CancellationToken cancellationToken)
        {
            if (_agentController is null)
            {
                throw new InvalidOperationException("Agent controller is unavailable.");
            }

            if (preset == AgentPermissionPreset.Standard
                && string.Equals(_agentPresetSessionId, _activeSessionId, StringComparison.Ordinal)
                && _agentPreset == AgentPermissionPreset.Standard
                && _agentPresetVersion is long knownStandardVersion)
            {
                return knownStandardVersion;
            }

            if (string.Equals(_agentPresetSessionId, _activeSessionId, StringComparison.Ordinal)
                && _agentPreset == preset
                && _agentPresetVersion is long knownVersion)
            {
                return knownVersion;
            }

            string wirePreset = preset switch
            {
                AgentPermissionPreset.Plan => "plan",
                AgentPermissionPreset.FullAccess => "full_access",
                _ => "standard",
            };
            string requestedSessionId = _activeSessionId;
            long requestedGeneration = _sessionOpenGeneration;
            long version = await SessionPresetVersionNegotiator.ExecuteAsync(
                requestedSessionId,
                expectedVersion => _agentController.SetSessionPresetAsync(
                    requestedSessionId,
                    wirePreset,
                    expectedVersion,
                    interactive: true,
                    cancellationToken),
                cancellationToken);
            if (requestedGeneration != _sessionOpenGeneration
                || !string.Equals(requestedSessionId, _activeSessionId, StringComparison.Ordinal))
            {
                throw new AgentApiException("session_changed", "Active session changed during permission update.");
            }

            _agentPresetSessionId = requestedSessionId;
            _agentPreset = preset;
            _agentPresetVersion = version;
            return version;
        }

        private async Task<bool> ConfirmFullAccessAsync()
        {
            if (XamlRoot is null)
            {
                return false;
            }

            var dialog = new ContentDialog
            {
                Title = "Allow Full Access for this session?",
                Content = "Full Access skips terminal approval prompts. Commands run as the current Windows user and may read or write outside the workspace or use the network. Process timeout, output, and cancellation controls still apply.",
                PrimaryButtonText = "Allow",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            return await ContentDialogGate.ShowAsync(dialog) == ContentDialogResult.Primary;
        }

        private bool IsProjectRootWorkspace(string? workspace)
        {
            if (string.IsNullOrWhiteSpace(workspace))
            {
                return true;
            }

            string normalizedWorkspace = workspace.Trim().TrimEnd('\\', '/');

            if (!string.IsNullOrWhiteSpace(_defaultAgentWorkspace))
            {
                string normDefault = _defaultAgentWorkspace.Trim().TrimEnd('\\', '/');
                if (string.Equals(normalizedWorkspace, normDefault, StringComparison.OrdinalIgnoreCase)
                    || normalizedWorkspace.StartsWith(normDefault + "\\", StringComparison.OrdinalIgnoreCase)
                    || normalizedWorkspace.StartsWith(normDefault + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            string currentDir = Environment.CurrentDirectory.Trim().TrimEnd('\\', '/');
            if (string.Equals(normalizedWorkspace, currentDir, StringComparison.OrdinalIgnoreCase)
                || normalizedWorkspace.StartsWith(currentDir + "\\", StringComparison.OrdinalIgnoreCase)
                || normalizedWorkspace.StartsWith(currentDir + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }


        private static bool IsTerminalStatus(string? status) => ChatSessionPresentation.IsTerminalStatus(status);

        private void ShowAgentError(Exception exception)
        {
            string status = exception switch
            {
                AgentApiException api => AgentFailurePresentation.RenderStartFailure(api.ErrorCode, api.Message, api.BlockingRunId, api.BlockingRunStatus),
                // A dialog collision is not a start failure: the gate absorbs it,
                // and if one ever slips through, say what to do instead of
                // reporting the run as broken.
                _ when ContentDialogGate.IsDialogConflict(exception) =>
                    "A dialog could not open because another one is still open. Close it and try again.",
                _ => $"Coding Agent could not start: {exception.Message}"
            };

            // A send that failed because no provider is configured is the first
            // failure a new user meets. Saying which provider is missing a key, and
            // offering the screen that fixes it, turns a dead end into one click.
            if (ProviderSetupGuidance.IsMissingKey(_currentSettings, _selectedBackend))
            {
                status = ProviderSetupGuidance.MissingKeyMessage(_currentSettings, _selectedBackend);
                ShowAgentInlineStatus(status);
                ShowProviderSetupCard(status);
                AgentStopButton.Visibility = Visibility.Collapsed;
                ApprovalPanel.Hide();
                QuestionPanel.Hide();
                return;
            }

            ShowAgentInlineStatus(status);
            AgentStopButton.Visibility = Visibility.Collapsed;
            ApprovalPanel.Hide();
            QuestionPanel.Hide();
        }

        /// <summary>
        /// Puts the provider-setup message in the transcript with a button that
        /// opens the screen where a key is added.
        /// </summary>
        private void ShowProviderSetupCard(string content)
        {
            Messages.Add(new ChatMessageItem
            {
                Role = "assistant",
                Content = content,
                IsError = true,
                ActionLabel = AgentFailurePresentation.OpenProviderSettingsLabel,
                ActionKind = AgentFailurePresentation.OpenProviderSettingsAction,
            });
            ScrollChatToBottom();
        }

        /// <summary>
        /// Raised when an error message offers to open provider settings. The window
        /// owns the settings surface, so it performs the navigation.
        /// </summary>
        public event EventHandler? ProviderSettingsRequested;
    }
}
