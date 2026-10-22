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
using Windows.Storage.Streams;
using System.IO;

namespace RuApp.Views
{
    public sealed partial class ChatView
    {
        private bool _isSessionHistoryLoading;
        /// <summary>
        /// Applies the conversation typography to a message row once its template has
        /// been realised.
        /// </summary>
        /// <remarks>
        /// Set from code rather than bound in markup. A binding inside a data template
        /// cannot reach the typography by name: x:Bind is compiled before the resource
        /// exists, and a classic Binding with a StaticResource source asks the template
        /// to resolve an application resource while it is being parsed. The singleton is
        /// directly reachable here, which removes that lookup entirely.
        ///
        /// Both MarkdownBlock and TextBlock raise a change callback when FontSize is
        /// set, so a message already on screen re-renders when the size changes.
        /// </remarks>
        private void ApplyTranscriptTypography(DependencyObject root)
        {
            var typography = TranscriptTypography.Instance;

            foreach (var element in EnumerateDescendants(root))
            {
                switch (element)
                {
                    case MarkdownBlock reply:
                        reply.FontSize = typography.MessageFontSize;
                        break;
                    case TextBlock { Name: "AgentActivityText" or "ThinkingLabel" or "EditPreviewLabel" } activity:
                        activity.FontSize = typography.ActivityFontSize;
                        break;
                }
            }
        }

        private static IEnumerable<DependencyObject> EnumerateDescendants(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                yield return child;
                foreach (var descendant in EnumerateDescendants(child))
                {
                    yield return descendant;
                }
            }
        }

        /// <summary>
        /// Re-applies the shared conversation typography to the composer and every
        /// realised message row, for a size change made while the transcript is on
        /// screen.
        /// </summary>
        private void RefreshTranscriptTypography()
        {
            if (ChatInputTextBox is not null)
            {
                ChatInputTextBox.FontSize = TranscriptTypography.Instance.MessageFontSize;
            }

            if (ChatListView is null)
            {
                return;
            }

            foreach (object entry in ChatListView.Items)
            {
                if (ChatListView.ContainerFromItem(entry) is DependencyObject container)
                {
                    ApplyTranscriptTypography(container);
                }
            }
        }

        private void OnTranscriptTypographyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(TranscriptTypography.MessageFontSize)
                or nameof(TranscriptTypography.ActivityFontSize))
            {
                RefreshTranscriptTypography();
            }
        }

        private void OnMessageActionClick(object sender, RoutedEventArgs e)        {
            if (sender is not FrameworkElement element
                || element.Tag is not string action
                || !string.Equals(
                    action,
                    AgentFailurePresentation.OpenProviderSettingsAction,
                    StringComparison.Ordinal))
            {
                return;
            }

            ProviderSettingsRequested?.Invoke(this, EventArgs.Empty);
        }

        private bool HasRenderedAgentReply() =>
            _activeAgentReplyReceived
            || (_activeAgentAssistantMessage is { Content: { } content }
                && !string.IsNullOrWhiteSpace(content));

        private void StartAgentMonitor()
        {
            if (_agentController is not { } controller) return;
            _ = _sessionPresentation.StartMonitoring(
                controller, CompleteActiveAgentTurnAsync, UpdateAgentInlineState,
                exception => ShowAgentInlineStatus($"Could not refresh the Coding Agent run: {exception.Message}"));
        }

        private async Task<string?> ReloadSessionHistoryAsync(CancellationToken cancellationToken, bool preserveLiveTurn = true)
        {
            if (_apiService is null)
            {
                return null;
            }

            var sessionId = _activeSessionId;
            var sessionGeneration = _sessionOpenGeneration;
            var assistant = _activeAgentAssistantMessage;
            var runId = _activeAgentRunId;
            bool OwnsReload() => !_scrollingDisposed && !cancellationToken.IsCancellationRequested
                && sessionGeneration == _sessionOpenGeneration
                && string.Equals(sessionId, _activeSessionId, StringComparison.Ordinal)
                && string.Equals(runId, _activeAgentRunId, StringComparison.Ordinal)
                && ReferenceEquals(assistant, _activeAgentAssistantMessage);
            string? streamedReply = _activeAgentAssistantMessage is { Content: { } content }
                ? StripDisplayMarkers(content)
                : null;
            var history = await _apiService.GetSessionHistoryAsync(sessionId, cancellationToken);
            if (history is null || !OwnsReload())
            {
                return null;
            }

            var projectedHistory = SessionHistoryProjection.Project(history.Select(row => (row.Role, row.Content)));
            var normalizedHistory = projectedHistory
                .Select(row => (row.Role, row.Content))
                .ToList();
            string? latestAssistant = projectedHistory
                .LastOrDefault(row => string.Equals(row.Role, "assistant", StringComparison.Ordinal))
                ?.Content ?? string.Empty;
            bool preserveStreamedReply = preserveLiveTurn && ChatHistoryReloadPolicy.ShouldPreserveStreamedReply(
                normalizedHistory,
                streamedReply);

            // Durable replay: messages carry source_run_id, while a fresh
            // load would drop every tool row. Rebuild settled rows from each terminal
            // run's durable events; the live turn keeps its in-memory rows below.
            var replayedRuns = _apiService is { } replayApi
                ? await AgentActivityReplayLoader.LoadAsync(
                    sessionId, replayApi.ListSessionRunIdsAsync, replayApi.ListRunActivitiesAsync, cancellationToken)
                : Array.Empty<SessionRunReplay>();

            void Apply()
            {
                _suppressContextUsageUpdate = true;
                try
                {
                    if (preserveStreamedReply
                        && _activeAgentAssistantMessage is { } streamedMessage)
                    {
                        if (!Messages.Contains(streamedMessage))
                        {
                            Messages.Add(streamedMessage);
                        }

                        streamedMessage.Content = streamedReply ?? string.Empty;
                        int assistantIndex = Messages.IndexOf(streamedMessage);
                        var user = assistantIndex > 0 && Messages[assistantIndex - 1].Role == "user"
                            ? Messages[assistantIndex - 1] : null;
                        SessionTranscriptBuilder.BindPersistedLiveTurn(history, runId, user, streamedMessage);
                        streamedMessage.IsStreaming = false;
                        streamedMessage.Usage = _activeAgentUsage;
                        UpdateLiveTailText(streamedMessage);
                        ScrollChatToBottom();
                        UpdateViewMode();
                        return;
                    }

                    DetachAgentActivitiesFromMessages();
                    var restored = SessionTranscriptBuilder.Build(history, replayedRuns, sessionId);
                    if (preserveLiveTurn && runId is not null && AgentActivities.Count > 0)
                    {
                        var current = restored.LastOrDefault(item => item.Role == "assistant"
                            && string.Equals(item.SourceRunId, runId, StringComparison.Ordinal));
                        if (current is null)
                        {
                            current = new ChatMessageItem { Role = "assistant", SourceRunId = runId };
                            restored.Add(current);
                        }
                        if (!string.IsNullOrEmpty(streamedReply)) current.Content = streamedReply;
                        current.Usage = _activeAgentUsage;
                        current.AgentActivities = new ObservableCollection<AgentActivityItem>(AgentActivities);
                        current.ActivitySplitOffsets = TakeSplitOffsetSnapshot(AgentActivities);
                        RebuildMessageSegments(current);
                    }
                    _sidePanelEditsDirty = true;
                    UpdateTodoPanel(_agentActivityFeed.RegisterReplayedMessages(restored));

                    var scroller = ChatScrollViewer;
                    double? preservedOffset = scroller is not null && scroller.ScrollableHeight > 0
                        ? scroller.VerticalOffset
                        : null;
                    bool wasFollowing = _followOutput;
                    var preservedAnchor = CaptureTranscriptAnchor();
                    Messages.ReplaceAll(restored);
                    RefreshSidePanelEdits();

                    if (history.Count > 0)
                    {
                        // The reset above drops the virtualized panel to its top:
                        // a reader parked mid-history keeps their place once layout
                        // has run, while a following reader still lands latest.
                        double restore = preservedOffset ?? 0;
                        RunAfterLayout(() =>
                        {
                            if (wasFollowing)
                            {
                                ScrollToLatestAfterLayout();
                                return;
                            }

                            RestoreTranscriptAnchor(preservedAnchor, restore);
                            _followOutput = false;
                        });
                    }

                    UpdateViewMode();
                }
                finally
                {
                    _suppressContextUsageUpdate = false;
                    UpdateContextUsage();
                }
            }

            bool didApply = await Services.OwnedUiUpdate.ApplyAsync(
                DispatcherQueue.HasThreadAccess ? null : action => DispatcherQueue.TryEnqueue(() => action()),
                OwnsReload,
                Apply);

            return didApply ? (preserveStreamedReply ? streamedReply : latestAssistant) : null;
        }

        private async void OnImageAttachmentClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ImageAttachmentItem item && item.PreviewImage is not null && XamlRoot is not null)
            {
                var image = new Image
                {
                    Source = item.PreviewImage,
                    Stretch = Stretch.Uniform,
                    MaxWidth = 800,
                    MaxHeight = 600,
                };
                var dialog = new ContentDialog
                {
                    Title = string.IsNullOrWhiteSpace(item.FileName) ? "Attached image" : item.FileName,
                    Content = new ScrollViewer
                    {
                        Content = image,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    },
                    CloseButtonText = "Close",
                    XamlRoot = XamlRoot,
                };
                try
                {
                    await ContentDialogGate.ShowAsync(dialog);
                }
                catch (Exception ex)
                {
                    // WinUI throws when a dialog opens while another one is
                    // still up; an async void handler would take the dispatcher
                    // down with it.
                    GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Image preview dialog failed: {ex.Message}");
                    ShowAgentInlineStatus("Could not open the image preview.");
                }
            }
        }


        // The composer view model owns unsent text and attachments per session.
        private void RestoreComposerDraft(string sessionId)
        {
            Composer.RestoreDraft(sessionId);
            foreach (var attachment in Composer.Attachments)
            {
                attachment.EnsurePreviewFromBase64();
            }
        }

        /// <summary>Writes text the composer view model replaced (a restore or a clear) into the box.</summary>
        private void OnComposerTextReplaced(string text)
        {
            if (ChatInputTextBox is not null
                && !string.Equals(ChatInputTextBox.Text, text, StringComparison.Ordinal))
            {
                ChatInputTextBox.Text = text;
            }
        }

        private async Task DetachAgentForSessionSwitchAsync()
        {
            var sessionId = _activeSessionId;
            var generation = _sessionOpenGeneration;
            bool OwnsView() => !_scrollingDisposed && sessionId == _activeSessionId
                && generation == _sessionOpenGeneration;
            try { await (_agentController?.StopForSessionSwitchAsync() ?? Task.CompletedTask); }
            catch (InvalidOperationException)
            {
                if (OwnsView())
                    ShowAgentInlineStatus("The pending run could not be identified. Reconcile it with Stop before switching sessions.");
                throw;
            }
            if (!OwnsView()) return;
            SuspendAgentFeedUpdates();
            AbandonActiveAgentReply(preservePresentation: true);
            UpdateSendButtonMode(isAgentRunning: false);
        }

        private async Task StopActiveAgentAsync()
        {
            await RunAgentActionAsync(
                () => _agentController?.CancelAsync() ?? Task.CompletedTask,
                "Stopping Coding Agent…");
            // Stopping is an interrupt: queued turns stay put until the user asks for
            // them, instead of a new run starting underneath the click that meant stop.
            if (!Composer.Queue.IsEmpty)
            {
                Composer.Queue.HoldAfterStop();
            }
            AbandonActiveAgentReply();
            UpdateSendButtonMode(isAgentRunning: false);
        }

        private async void OnAgentStopClick(object sender, RoutedEventArgs e)
        {
            await StopActiveAgentAsync();
        }

        private async void OnApprovalDecisionRequested(object? sender, Controls.AgentApprovalDecidedEventArgs e)
        {
            if (_agentController is not { } controller) return;
            string? runId = controller.CurrentRun?.RunId;
            if (runId is null) return;
            await _sessionHistoryPresenter.RunApprovalAsync(
                e.Approved,
                e.Scope,
                operation => operation switch
                {
                    AgentApprovalOperation.Deny => controller.DenyAsync(),
                    AgentApprovalOperation.AllowSessionDirectory => controller.ApproveSessionDirectoryAsync(),
                    AgentApprovalOperation.AllowAlways => controller.ApproveAlwaysAsync(),
                    _ => controller.ApproveAsync(),
                },
                () => !_scrollingDisposed && ReferenceEquals(controller, _agentController)
                    && string.Equals(controller.CurrentRun?.RunId, runId, StringComparison.Ordinal),
                ShowAgentInlineStatus,
                UpdateAgentInlineState,
                StartAgentMonitor,
                ShowAgentError,
                ClearComposerStatus);
        }

        private async void OnQuestionAnswerRequested(object? sender, Controls.AgentQuestionAnsweredEventArgs e)
        {
            if (_agentController is not { } controller) return;
            string? runId = controller.CurrentRun?.RunId;
            if (runId is null) return;
            // The controller owns the resolve route and clears its question card
            // only after a successful POST. The presenter owns the interaction lifecycle.
            await _sessionHistoryPresenter.RunInputAsync(
                e.Answer,
                answer => controller.SubmitInputAsync(answer),
                () => !_scrollingDisposed && ReferenceEquals(controller, _agentController)
                    && string.Equals(controller.CurrentRun?.RunId, runId, StringComparison.Ordinal),
                ShowAgentInlineStatus,
                UpdateAgentInlineState,
                StartAgentMonitor,
                ShowAgentError,
                ClearComposerStatus);
        }

        private async Task RunAgentActionAsync(Func<Task> action, string status)
        {
            if (_agentController is null || !_sessionHistoryPresenter.TryBeginAction()) return;
            ShowAgentInlineStatus(status);
            UpdateAgentInlineState();
            try
            {
                await action();
                StartAgentMonitor();
            }
            catch (Exception exception)
            {
                ShowAgentError(exception);
            }
            finally
            {
                _sessionHistoryPresenter.EndAction();
                ClearComposerStatus();
            }
        }

        /// <summary>Overrides the workspace for the next runs; null restores the app root default.</summary>
        public void SetRequestedWorkspace(string? workspacePath)
        {
            _requestedWorkspace = string.IsNullOrWhiteSpace(workspacePath) ? null : workspacePath.Trim();
        }

        public async Task OpenSessionAsync(string sessionId, string title)       {
            // Visible to the catch below: a superseded load must not paint its
            // failure into the session that replaced it.
            long generation = -1;
            try
            {
                if (_apiService == null) return;
                var openedSession = await _sessionPresentation.OpenAsync(
                    sessionId,
                    () => RetainComposerDraft(checkOnly: true),
                    DetachAgentForSessionSwitchAsync,
                    () => RetainComposerDraft(checkOnly: false));
                if (openedSession is not { } open) return;
                generation = open.Generation;
                _isSessionHistoryLoading = true;
                var openToken = open.Cancellation;
                _activeSessionTitle = string.IsNullOrWhiteSpace(title) ? "New Chat" : title;
                _agentWorkspace = AgentWorkspaceSelection.Resolve(_requestedWorkspace, _defaultAgentWorkspace)
                    ?? _agentWorkspace;
                ResetAgentState(preserveTranscript: true);
                _sidePanelRunRunning = null;
                UpdateSidePanel(false);
                // A preset belongs to the session it was set for. The send path
                // pushes the composer's preset onto whichever session is open,
                // so carrying Full Access across would silently elevate a
                // session the user never chose it for. Plan and Standard are
                // preferences and may follow the user; an elevation may not.
                if (Presets.NarrowForOpening(sessionId))
                {
                    UpdateAgentInlineState();
                }

                RestoreComposerDraft(sessionId);
                SessionOpened?.Invoke(this, new SessionOpenedEventArgs(sessionId, title));
                // Bulk load: one reset notification for the whole transcript
                // instead of one add notification per row.
                var opened = new List<ChatMessageItem>();
                // Every await below is a chance for another switch to take over. Opening a
                // session out of order used to paint its history under the newer session's
                // title, and the backend's active session (activation is a POST) could end
                // up on a different session than the one on screen.
                var history = await _apiService.GetSessionHistoryAsync(sessionId, openToken);
                if (!IsCurrentOpen(generation)) return;
                if (history is null)
                    throw new InvalidOperationException("Session history is unavailable.");
                // Durable replay: messages carry source_run_id, while a session
                // opened fresh would show a transcript of prose with every tool row missing.
                // Rebuild the settled rows from each terminal run's durable events. This is
                // what makes the activity feed survive leaving the session or closing the app;
                // without it the rows exist only as transient frames and are gone the moment
                // the live connection ends.
                var replayedRuns = await AgentActivityReplayLoader.LoadAsync(
                    sessionId, _apiService.ListSessionRunIdsAsync, _apiService.ListRunActivitiesAsync, openToken);
                if (!IsCurrentOpen(generation)) return;
                _sidePanelTasksRunId = replayedRuns.LastOrDefault()?.RunId;
                UpdateAgentInlineState();
                _ = RefreshAgentTasksAsync();

                if (history != null)
                {
                    opened = SessionTranscriptBuilder.Build(history, replayedRuns, sessionId);
                    // No await between clearing the old feed and replacing its rows:
                    // the outgoing transcript remains intact throughout history I/O.
                    ClearAgentActivities();
                    _sidePanelEditsDirty = true;
                    UpdateTodoPanel(_agentActivityFeed.RegisterReplayedMessages(opened));
                    JumpToLatestForNewTurn();
                    Messages.ReplaceAll(opened);
                    RefreshSidePanelEdits();

                    if (history.Count > 0)
                    {
                        PositionOpenedTranscript();
                    }
                }

                // Restore the latest step's report and optional request breakdown,
                // not cumulative session billing. The forced repaint below adopts
                // its originating run's context capacity.
                var restoredUsage = await _apiService.GetLatestSessionUsageAsync(sessionId, openToken);
                if (restoredUsage is not null && IsCurrentOpen(generation))
                {
                    _activeAgentUsage = restoredUsage;
                    for (int index = Messages.Count - 1; index >= 0; index--)
                    {
                        var item = Messages[index];
                        if (string.Equals(item.Role, "assistant", StringComparison.Ordinal)
                            && item.Usage is null)
                        {
                            item.Usage = restoredUsage;
                            break;
                        }
                    }
                }

                // A run that outlived the host is invisible until it is adopted:
                // re-arm Continue/Stop for this session's newest non-terminal run.
                // Bounded and fail-soft; a read failure never fails the open.
                await TryAdoptSessionRunAsync(sessionId, openToken, generation);
                if (IsCurrentOpen(generation))
                {
                    // The session keeps the preset it was stored with: without
                    // this a plan session reopened in the GUI shows whatever the
                    // previous session chose, and the next send silently
                    // re-grades it. A missing or unreadable selection leaves the
                    // current choice untouched.
                    await AdoptStoredSessionPresetAsync(sessionId, openToken);
                }
            }
            catch (OperationCanceledException)
            {
                // A newer open superseded this load: its token cancelled the I/O
                // on purpose, and the newer session owns the screen.
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"Error in OpenSessionAsync: {ex.Message}");
                // A superseded load must not paint its failure into the session
                // that replaced it.
                if (!IsCurrentOpen(generation))
                {
                    return;
                }

                ClearAgentActivities();
                Messages.ReplaceAll(new[] { new ChatMessageItem
                {
                    Role = "assistant",
                    Content = "**SESSION LOAD ERROR:** Could not load chat session. Please check backend and try again.",
                    IsError = true
                } });
            }
            finally
            {
                if (IsCurrentOpen(generation))
                {
                    _isSessionHistoryLoading = false;
                    UpdateContextUsage(force: true);
                }
            }
        }

        /// <summary>
        /// Adopts the stored permission preset of the session being opened, so
        /// the Settings dropdown and the next send agree with the durable
        /// choice instead of the previous session's. Best effort and bounded:
        /// any read failure, an outdated response, or a missing selection
        /// leaves the current choice untouched — never a surprise swap.
        /// </summary>
        private async Task AdoptStoredSessionPresetAsync(string sessionId, CancellationToken cancellationToken)
        {
            if (await Presets.AdoptStoredAsync(sessionId, () => _activeSessionId, cancellationToken) is not { } preset)
            {
                return;
            }

            UpdateAgentInlineState();
            SessionPresetAdopted?.Invoke(this, new SessionPresetAdoptedEventArgs(sessionId, preset));
        }

        /// <summary>
        /// Adopts the session's newest non-terminal run when the controller is not
        /// already tracking a run for it. After a host restart a parked run must
        /// still offer Continue/Cancel; adoption re-arms exactly that run. Best
        /// effort and bounded: any read failure leaves the open as it would have been.
        /// </summary>
        private async Task TryAdoptSessionRunAsync(string sessionId, CancellationToken cancellationToken, long generation)
        {
            var controller = _agentController;
            if (controller is null
                || _apiService is null
                || string.IsNullOrWhiteSpace(sessionId)
                || !string.Equals(sessionId, _activeSessionId, StringComparison.Ordinal))
            {
                return;
            }

            // A live run in hand for this session already owns the command surface;
            // the open path must not replace it with an older parked one.
            if (controller.CurrentRun is { } tracked
                && string.Equals(tracked.SessionId, sessionId, StringComparison.Ordinal)
                && !IsTerminalStatus(tracked.Status))
            {
                return;
            }

            try
            {
                var runs = await _apiService.ListSessionRunIdsAsync(sessionId, cancellationToken);
                if (runs is not { Count: > 0 }
                    || !IsCurrentOpen(generation)
                    || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // The reader reverses the wire's newest-first order, so the newest
                // run is the tail.
                var newest = runs[^1];
                if (newest.IsTerminal)
                {
                    return;
                }

                var run = await _apiService.GetRunAsync(newest.RunId, cancellationToken);
                if (run is null
                    || IsTerminalStatus(run.Status)
                    || !string.Equals(run.SessionId, sessionId, StringComparison.Ordinal))
                {
                    return;
                }

                // The open may have been superseded while the run read was in flight:
                // latching the old session's run here would follow a run the new
                // session never asked for, and its reference would keep the new
                // session from adopting its own.
                if (!IsCurrentOpen(generation) || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var adopted = await controller.AdoptRunAsync(run, cancellationToken);
                if (!adopted
                    || !IsCurrentOpen(generation)
                    || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // A waiting_input run's question arrived before the adoption cursor,
                // so re-arm the card from the run's durable event log; otherwise the
                // run waits with nothing to answer.
                if (string.Equals(run.Status, "waiting_input", StringComparison.Ordinal))
                {
                    var pendingInput = await _apiService.GetPendingRunInputAsync(run.RunId, cancellationToken);
                    if (pendingInput is not null
                        && IsCurrentOpen(generation)
                        && !cancellationToken.IsCancellationRequested)
                    {
                        controller.RestorePendingInput(
                            pendingInput.InputId,
                            pendingInput.Question,
                            pendingInput.Options);
                    }
                }

                // A live run needs the monitor so its terminal frame settles the
                // transcript; a parked run waits for the typed Continue, whose own
                // action starts the monitor.
                if (!IsParkedRunStatus(run.Status))
                {
                    StartAgentMonitor();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Could not adopt the session run: {ex.Message}");
            }
        }

        private static bool IsParkedRunStatus(string status) =>
            string.Equals(status, "waiting_user", StringComparison.Ordinal)
            || string.Equals(status, "reconciliation_required", StringComparison.Ordinal);

        /// <summary>
        /// True while the attempt that started as <paramref name="generation"/> is still the
        /// one whose session is open. A superseded attempt must not touch the transcript.
        /// </summary>
        private bool IsCurrentOpen(long generation) => _sessionPresentation.IsCurrent(generation);

        private void ActivateSession(string sessionId) => _sessionPresentation.Activate(sessionId);

        private bool RetainComposerDraft(bool checkOnly) =>
            Composer.RetainDraft(_activeSessionId, ChatInputTextBox?.Text ?? string.Empty, checkOnly);

        private static double HorizontalInsetFor(double width) =>
            width < NarrowWidthThreshold
                ? NarrowHorizontalInset
                : width < MediumWidthThreshold
                    ? MediumHorizontalInset
                    : WideHorizontalInset;

        private void ApplyInputWidth()
        {
            if (RootGrid == null || RootGrid.ActualWidth <= 0) return;
            TodoPanelScroll.MaxHeight = AgentTodoPanelPresentation.Create(_latestTodos, RootGrid.ActualHeight).ListHeight;

            UpdateSidePanelLayout();

            double horizontalInset = HorizontalInsetFor(RootGrid.ActualWidth);
            double availableWidth = Math.Max(
                MinAvailableInputWidth,
                RootGrid.ActualWidth - horizontalInset - _sidePanelReserveWidth);
            double targetWidth = Math.Min(ReadingColumnMaxWidth, availableWidth);

            if (WelcomeHeroPanel != null)
            {
                WelcomeHeroPanel.Width = targetWidth;
            }

            if (InputContainer != null)
            {
                InputContainer.Width = targetWidth;
                InputContainer.Margin = new Thickness(16, 0, 16 + _sidePanelReserveWidth, 0);
            }

            if (InputBorder != null)
            {
                InputBorder.Width = targetWidth;
            }

            // The transcript scroll host stays full-width so its scrollbar docks
            // at the window edge; each message root centers its own 820 column.
            LayoutRealizedMessageColumns();
        }

        private void OnMessageColumnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement column)
            {
                // The row's template is realised by now, so its text elements exist and
                // can take the conversation size.
                ApplyTranscriptTypography(column);
                LayoutMessageColumn(column);
            }
        }

        /// <summary>
        /// Sizes the message column before its first arrange. Loaded fires after the
        /// row already measured itself against its content, so a fresh row flashes
        /// narrow and then jumps wide once Loaded sets the width. Sizing here keeps
        /// every painted frame at its final width; Loaded stays as the fallback.
        /// </summary>
        private void OnChatContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }

            if (FindMessageColumn(args.ItemContainer) is { } column)
            {
                LayoutMessageColumn(column);
                return;
            }

            args.RegisterUpdateCallback((_, callbackArgs) =>
            {
                if (FindMessageColumn(callbackArgs.ItemContainer) is { } lateColumn)
                {
                    LayoutMessageColumn(lateColumn);
                }
            });
        }

        private static FrameworkElement? FindMessageColumn(SelectorItem? container) =>
            (container?.ContentTemplateRoot as FrameworkElement)?.FindName("MessageColumn") as FrameworkElement;

        private void LayoutMessageColumn(FrameworkElement column)
        {
            if (ChatListView is null || ChatListView.ActualWidth <= 0)
            {
                return;
            }

            double viewport = Math.Max(
                0,
                ChatListView.ActualWidth - ChatListView.Padding.Left - ChatListView.Padding.Right - _sidePanelReserveWidth);
            column.Width = Math.Min(MessageColumnMaxWidth, viewport);
            column.Margin = new Thickness(0, 0, _sidePanelReserveWidth, 0);
        }

        private void LayoutRealizedMessageColumns()
        {
            if (ChatListView is null || Messages.Count == 0)
            {
                return;
            }

            for (var i = 0; i < Messages.Count; i++)
            {
                if (ChatListView.ContainerFromIndex(i) is ListViewItem container
                    && container.ContentTemplateRoot is FrameworkElement root
                    && root.FindName("MessageColumn") is FrameworkElement column)
                {
                    LayoutMessageColumn(column);
                }
            }
        }
        private void OnCopyMessageContentClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is ChatMessageItem msg && !string.IsNullOrEmpty(msg.Content))
            {
                try
                {
                    var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    package.SetText(msg.Content);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                }
                catch (Exception ex)
                {
                    GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Error copying content: {ex.Message}");
                }
            }
        }

        private async void OnRewindToMessageClick(object sender, RoutedEventArgs e)
        {
            if (_apiService is not { } api || sender is not FrameworkElement { Tag: ChatMessageItem message }) return;
            var sessionId = _activeSessionId;
            var generation = _sessionOpenGeneration;
            bool OwnsSession() => !_scrollingDisposed && generation == _sessionOpenGeneration
                && sessionId == _activeSessionId && ReferenceEquals(api, _apiService);
            await _sessionHistoryPresenter.RewindAsync(
                sessionId,
                message.SourceMessageIndex,
                _isSessionHistoryLoading,
                IsActiveAgentRunPending(),
                Messages.Contains(message) && message.SessionId == sessionId,
                OwnsSession,
                api.RewindSessionAsync,
                async () => await ReloadSessionHistoryAsync(CancellationToken.None, preserveLiveTurn: false) is not null,
                _sessionHistoryPresenter.IsActionPending,
                () => ShowAgentInlineStatus("Finish or stop the current turn before rewinding."),
                () => ShowAgentInlineStatus("This row has no saved message to rewind to. Reopen the session to refresh history."),
                () => ShowAgentInlineStatus("Could not rewind the session. Try again."),
                ex => GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Error rewinding session: {ex.Message}"));
        }

        public void Dispose()
        {
            SidePanel.SubagentsChanged -= OnSubagentsChanged;
        SidePanel.DisposeSubagents();
            ModelSelection.PickerProjectionReady -= OnModelSelectionPickerReady;
            ModelSelection.SelectionChanged -= OnModelSelectionChanged;
            ModelSelection.Dispose();
            RunPanel.PropertyChanged -= OnRunPanelPropertyChanged;
            RunPanel.ApprovalArrived -= OnRunPanelApprovalArrived;
            RunPanel.QuestionArrived -= OnRunPanelQuestionArrived;
            RunPanel.TrustPromptRequested -= OnRunPanelTrustPromptRequested;
            StopTaskRefreshTimer();
            SidePanel?.CancelVisibilityAnimations();
            _taskRefreshCancellation?.Cancel();
            _taskRefreshCancellation?.Dispose();
            _taskRefreshCancellation = null;
            DisposeChatScrolling();
            SidePanel?.ShutdownTerminal();
            SidePanel?.ShutdownPreview();
            _sessionPresentation.Dispose();
            if (_contextMeterTimer is not null)
            {
                _contextMeterTimer.Tick -= OnContextMeterTimerTick;
                _contextMeterTimer.Stop();
                _contextMeterTimer = null;
            }
            _contextMeterScheduled = false;
            TranscriptTypography.Instance.PropertyChanged -= OnTranscriptTypographyChanged;
            Composer.Queue.Items.CollectionChanged -= OnQueuedTurnsChanged;
            Composer.TextReplaced -= OnComposerTextReplaced;
            Messages.CollectionChanged -= OnMessagesChanged;
            RootGrid.SizeChanged -= OnRootGridSizeChanged;
            ChatListView.ContainerContentChanging -= OnChatContainerContentChanging;
            this.Loaded -= OnChatViewLoaded;
            if (_providerCatalogController != null)
            {
                var catalog = _providerCatalogController;
                bool owns = _ownsProviderCatalogController;
                _providerCatalogController = null;
                if (owns)
                {
                    // Never block the UI thread on async disposal: every await
                    // inside the controller uses ConfigureAwait(false), so the
                    // task completes independently and a fault is only logged.
                    _ = catalog.DisposeAsync().AsTask().ContinueWith(
                        static faulted => GuiDiagnosticWriter.AppendTraceLine(
                            $"[ChatView] Catalog dispose failed: {faulted!.Exception!.GetBaseException().Message}"),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                }
            }
            if (_groupedCatalogs != null)
            {
                var grouped = _groupedCatalogs;
                bool ownsGrouped = _ownsGroupedCatalogs;
                _groupedCatalogs = null;
                if (ownsGrouped)
                {
                    _ = grouped.DisposeAsync().AsTask().ContinueWith(
                        static faulted => GuiDiagnosticWriter.AppendTraceLine(
                            $"[ChatView] Grouped catalog dispose failed: {faulted!.Exception!.GetBaseException().Message}"),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                }
            }
            if (_agentController is not null)
            {
                _agentController.PropertyChanged -= OnAgentControllerChanged;
                _agentController.ActivityReceived -= OnAgentActivityReceived;
                _agentController.SummaryReceived -= OnAgentSummaryReceived;
                _agentController.AssistantDeltaReceived -= OnAgentAssistantDeltaReceived;
                _agentController.ToolOutputReceived -= OnAgentToolOutputReceived;
                _agentController.UsageReceived -= OnAgentUsageReceived;
                _agentController.ContextCompacted -= OnAgentContextCompacted;
                _agentController.ContextEstimateReceived -= OnContextEstimateReceived;
                _agentController.RunAdopted -= OnAgentRunAdopted;
            }
            StopAllTextSweeps();
            DetachWaveTimer();
            DetachContextHoverTimer();
            ClearAgentActivities();
            if (_activityFlushTimer != null)
            {
                _activityFlushTimer.Stop();
                _activityFlushTimer.Tick -= OnActivityFlushTick;
                _activityFlushTimer = null;
            }
            MeteorShowerField?.Dispose();
        }

        private void DetachWaveTimer()
        {
            if (_waveTimer != null)
            {
                _waveTimer.Tick -= OnWaveTimerTick;
                _waveTimer = null;
            }
        }

        private void DetachContextHoverTimer()
        {
            if (AttachImageButton is not null)
            {
                AttachImageButton.PointerEntered -= OnAttachImagePointerEntered;
                AttachImageButton.PointerExited -= OnAttachImagePointerExited;
            }
            if (ContextMeterButton is not null)
            {
                ContextMeterButton.PointerEntered -= OnContextMeterPointerEntered;
                ContextMeterButton.PointerExited -= OnContextMeterPointerExited;
            }
            if (_contextHoverTimer is not null)
            {
                _contextHoverTimer.Tick -= OnContextHoverTimerTick;
                _contextHoverTimer.Stop();
                _contextHoverTimer = null;
            }
        }
    }
}
