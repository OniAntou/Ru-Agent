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
                    case TextBlock { Name: "AgentActivityText" } activity:
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
        /// Re-applies typography to every realised message row, for a size change made
        /// while the transcript is on screen.
        /// </summary>
        private void RefreshTranscriptTypography()
        {
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
            var previous = _agentMonitorCts;
            _agentMonitorCts = new CancellationTokenSource();
            previous?.Cancel();
            previous?.Dispose();
            _ = MonitorAgentRunAsync(_agentMonitorCts.Token);
        }

        private async Task MonitorAgentRunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && _agentController is not null)
                {
                    await Task.Delay(Services.UiTiming.AgentRetryDelay, cancellationToken);
                    if (_agentController.CurrentRun is null)
                    {
                        return;
                    }

                    if (IsTerminalStatus(_agentController.CurrentRun.Status))
                    {
                        await CompleteActiveAgentTurnAsync(cancellationToken);
                        return;
                    }

                    await _agentController.RefreshAsync(cancellationToken);
                    UpdateAgentInlineState();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                // The run may still be alive server-side, so this is a refresh
                // failure, not a start failure: say exactly that instead of
                // dropping it behind the start-failure copy.
                ShowAgentInlineStatus($"Could not refresh the Coding Agent run: {exception.Message}");
            }
        }

        private async Task<string?> ReloadSessionHistoryAsync(CancellationToken cancellationToken)
        {
            if (_apiService is null)
            {
                return null;
            }

            var sessionId = _activeSessionId;
            string? streamedReply = _activeAgentAssistantMessage is { Content: { } content }
                ? StripDisplayMarkers(content)
                : null;
            var history = await _apiService.GetSessionMessagesAsync(sessionId);
            if (history is null || cancellationToken.IsCancellationRequested || !string.Equals(sessionId, _activeSessionId, StringComparison.Ordinal))
            {
                return null;
            }

            var projectedHistory = SessionHistoryProjection.Project(history);
            var normalizedHistory = projectedHistory
                .Select(row => (row.Role, row.Content))
                .ToList();
            string? latestAssistant = projectedHistory
                .LastOrDefault(row => string.Equals(row.Role, "assistant", StringComparison.Ordinal))
                ?.Content ?? string.Empty;
            int lastAssistantIndex = SessionHistoryProjection.LastAssistantIndex(history);
            bool preserveStreamedReply = ChatHistoryReloadPolicy.ShouldPreserveStreamedReply(
                normalizedHistory,
                streamedReply);

            // Durable replay: session messages persist only role/content, so a fresh
            // load would drop every tool row. Rebuild settled rows from each terminal
            // run's durable events; the live turn keeps its in-memory rows below.
            var replayedRuns = await LoadReplayedRunActivitiesAsync(sessionId, cancellationToken);

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
                        streamedMessage.IsStreaming = false;
                        streamedMessage.Usage = _activeAgentUsage;
                        UpdateLiveTailText(streamedMessage);
                        ScrollChatToBottom();
                        UpdateViewMode();
                        return;
                    }

                    var existingActivities = Messages
                        .Where(m => m.Role == "assistant")
                        .Select(m => m.AgentActivities != null && !ReferenceEquals(m.AgentActivities, AgentActivities)
                            ? m.AgentActivities
                            : (ReferenceEquals(m.AgentActivities, AgentActivities) && AgentActivities.Count > 0
                                ? new ObservableCollection<AgentActivityItem>(AgentActivities)
                                : null))
                        .ToList();
                    var existingOffsets = Messages
                        .Where(m => m.Role == "assistant")
                        .Select(m => m.ActivitySplitOffsets
                            ?? (ReferenceEquals(m.AgentActivities, AgentActivities) && AgentActivities.Count > 0
                                ? TakeSplitOffsetSnapshot(AgentActivities)
                                : null))
                        .ToList();

                    DetachAgentActivitiesFromMessages();
                    Messages.Clear();
                    int assistantCount = 0;
                    // Live rows belong to the turn that just ended. They may only
                    // ride on history's last assistant when it is the final
                    // persisted message; otherwise the turn has no persisted
                    // assistant yet (deny/fail) and the rows need their own bubble.
                    bool attachLive = SessionHistoryProjection.AttachLiveToLastAssistant(
                        projectedHistory.Count, lastAssistantIndex, AgentActivities.Count);
                    bool liveAttached = false;
                    // Runs align to rendered assistant turns tail-first: the newest
                    // run belongs to the newest assistant message. A denied/failed
                    // turn with no persisted text shifts the head, never the tail.
                    int totalAssistants = projectedHistory.Count(row =>
                        string.Equals(row.Role, "assistant", StringComparison.Ordinal) && row.Render);
                    for (int index = 0; index < projectedHistory.Count; index++)
                    {
                        var row = projectedHistory[index];
                        // A machine tool envelope must never render as chat text
                        // (e.g. a repeated call that failed the run). Drop it.
                        if (!row.Render)
                        {
                            continue;
                        }

                        string role = row.Role;
                        string content = row.Content;

                        ObservableCollection<AgentActivityItem>? activities = null;
                        List<ActivitySplitOffset>? splitOffsets = null;
                        if (role == "assistant")
                        {
                            if (index == lastAssistantIndex)
                            {
                                if (attachLive)
                                {
                                    activities = new ObservableCollection<AgentActivityItem>(AgentActivities);
                                    splitOffsets = TakeSplitOffsetSnapshot(AgentActivities);
                                    liveAttached = true;
                                }
                                else
                                {
                                    activities = assistantCount < existingActivities.Count ? existingActivities[assistantCount] : null;
                                    splitOffsets = assistantCount < existingOffsets.Count ? existingOffsets[assistantCount] : null;
                                }
                            }
                            else if (assistantCount < existingActivities.Count)
                            {
                                activities = existingActivities[assistantCount];
                                splitOffsets = assistantCount < existingOffsets.Count ? existingOffsets[assistantCount] : null;
                            }

                            if (activities is null)
                            {
                                int replayIndex = Services.ReloadActivitiesPolicy.ReplayIndexForAssistant(
                                    replayedRuns.Count, totalAssistants, assistantCount);
                                if (replayIndex >= 0
                                    && replayedRuns[replayIndex].Count > 0)
                                {
                                    activities = new ObservableCollection<AgentActivityItem>(replayedRuns[replayIndex]);
                                    // Register by id so live frames for a still-running
                                    // run update these rows in place instead of
                                    // appending duplicates beside them.
                                    foreach (var replayed in activities)
                                    {
                                        _agentActivityById.TryAdd(replayed.ActivityId, replayed);
                                    }
                                }
                            }

                            assistantCount++;
                        }

                        var restoredMessage = new ChatMessageItem
                        {
                            Role = role,
                            Content = role == "assistant" ? StripDisplayMarkers(content) : content,
                            Usage = index == lastAssistantIndex ? _activeAgentUsage : null,
                            AgentActivities = activities,
                            ActivitySplitOffsets = splitOffsets,
                        };
                        RebuildMessageSegments(restoredMessage);
                        Messages.Add(restoredMessage);
                    }

                    if (Services.ReloadActivitiesPolicy.AppendOrphanedActivities(AgentActivities.Count, liveAttached))
                    {
                        // The ended turn has live rows but no persisted assistant
                        // (denied/failed before any reply text). Keep the rows in
                        // their own trailing bubble instead of dropping them.
                        var orphan = new ChatMessageItem
                        {
                            Role = "assistant",
                            Content = StripDisplayMarkers(streamedReply ?? string.Empty),
                            Usage = _activeAgentUsage,
                            AgentActivities = new ObservableCollection<AgentActivityItem>(AgentActivities),
                            ActivitySplitOffsets = TakeSplitOffsetSnapshot(AgentActivities),
                        };
                        RebuildMessageSegments(orphan);
                        Messages.Add(orphan);
                    }

                    if (history.Count > 0)
                    {
                        ScrollChatToBottom();
                    }

                    UpdateViewMode();
                }
                finally
                {
                    _suppressContextUsageUpdate = false;
                    UpdateContextUsage();
                }
            }

            if (DispatcherQueue.HasThreadAccess)
            {
                Apply();
            }
            else
            {
                var applied = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                if (!DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        Apply();
                        applied.TrySetResult(true);
                    }
                    catch (Exception error)
                    {
                        applied.TrySetException(error);
                    }
                }))
                {
                    return null;
                }

                await applied.Task.ConfigureAwait(false);
            }

            return preserveStreamedReply ? streamedReply : latestAssistant;
        }

        /// <summary>
        /// Rebuilds settled activity rows for every terminal run of the session,
        /// oldest-first and positionally aligned with the run list (a non-terminal
        /// or unreadable run keeps an empty placeholder so tail alignment holds).
        /// Best-effort: any failure yields empty rows and the text-only transcript.
        /// </summary>
        private async Task<IReadOnlyList<IReadOnlyList<AgentActivityItem>>> LoadReplayedRunActivitiesAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            var empty = Array.AsReadOnly(Array.Empty<IReadOnlyList<AgentActivityItem>>());
            if (_apiService is null || string.IsNullOrWhiteSpace(sessionId))
            {
                return empty;
            }

            var runs = await _apiService.ListSessionRunIdsAsync(sessionId, cancellationToken);
            if (runs is null || runs.Count == 0 || cancellationToken.IsCancellationRequested)
            {
                return empty;
            }

            var slots = new IReadOnlyList<AgentActivityItem>?[runs.Count];
            using var gate = new SemaphoreSlim(4);
            var tasks = new List<Task>(runs.Count);
            for (int index = 0; index < runs.Count; index++)
            {
                int slot = index;
                string runId = runs[slot].RunId;
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        var models = await _apiService.ListRunActivitiesAsync(runId, cancellationToken);
                        slots[slot] = models is null
                            ? Array.AsReadOnly(Array.Empty<AgentActivityItem>())
                            : models.Select(model => new AgentActivityItem(model)).ToArray();
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        slots[slot] = Array.AsReadOnly(Array.Empty<AgentActivityItem>());
                    }
                    catch (Exception)
                    {
                        slots[slot] = Array.AsReadOnly(Array.Empty<AgentActivityItem>());
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, cancellationToken));
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return empty;
            }

            return slots.Select(slot => slot ?? Array.AsReadOnly(Array.Empty<AgentActivityItem>())).ToArray();
        }


        private ScrollViewer? _chatScrollViewer;

        /// <summary>
        /// The ListView's own scroll host. The transcript ListView virtualizes,
        /// so scrolling, offsets and follow state all come from this host.
        /// Resolved lazily because the template part only exists after render.
        /// </summary>
        private ScrollViewer? ChatScrollViewer
        {
            get
            {
                if (_chatScrollViewer is null && ChatListView is not null)
                {
                    _chatScrollViewer = FindDescendantScrollViewer(ChatListView);
                }

                return _chatScrollViewer;
            }
        }

        private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
        {
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current is ScrollViewer viewer)
                {
                    return viewer;
                }

                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                {
                    queue.Enqueue(VisualTreeHelper.GetChild(current, index));
                }
            }

            return null;
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
                    await dialog.ShowAsync();
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


        private void ScrollChatToBottom()
        {
            var scroller = ChatScrollViewer;
            if (scroller is null)
            {
                QueueScrollRetry();
                return;
            }

            if (scroller.ScrollableHeight > 0)
            {
                double verticalOffset = scroller.VerticalOffset;
                double maxOffset = scroller.ScrollableHeight;
                bool nearBottom = maxOffset - verticalOffset <= Services.UiTiming.ChatFollowThresholdPx;
                if (nearBottom || _followOutput)
                {
                    // Streaming snapshots and activity batches can arrive dozens
                    // of times per second. A ChangeView forces a layout pass, so
                    // coalesce them: the eye cannot track faster than this, but
                    // the dispatcher queue can drown in it and stall the feed
                    // into visible bursts.
                    long now = Environment.TickCount64;
                    if (now - _lastScrollChangeTick >= ScrollChangeMinIntervalMs)
                    {
                        _lastScrollChangeTick = now;
                        scroller.ChangeView(null, maxOffset, null, disableAnimation: true);
                    }
                    else
                    {
                        QueueScrollRetry();
                    }
                }
                else
                {
                    ShowNewMessagesPill();
                }
                return;
            }
            QueueScrollRetry();
        }

        private void QueueScrollRetry()
        {
            if (_scrollRetryQueued)
            {
                return;
            }

            _scrollRetryQueued = true;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RetryScrollAfterLayout);
        }

        private void RetryScrollAfterLayout()
        {
            _scrollRetryQueued = false;
            var scroller = ChatScrollViewer;
            if (scroller is null)
            {
                return;
            }

            if (scroller.ScrollableHeight > 0)
            {
                double verticalOffset = scroller.VerticalOffset;
                double maxOffset = scroller.ScrollableHeight;
                bool nearBottom = maxOffset - verticalOffset <= Services.UiTiming.ChatFollowThresholdPx;
                if (nearBottom)
                {
                    _lastScrollChangeTick = Environment.TickCount64;
                    scroller.ChangeView(null, maxOffset, null, disableAnimation: true);
                }
                else
                {
                    ShowNewMessagesPill();
                }
            }
        }

        private long _lastScrollChangeTick;
        private bool _scrollRetryQueued;
        private const long ScrollChangeMinIntervalMs = 120;

        // The draft rule (what survives a session switch, what an empty box means)
        // lives in ComposerDraftStore; the view only moves text in and out of the box.
        private readonly ComposerDraftStore _composerDrafts = new();
        private bool _newMessagesPillWired;
        private bool _followOutput = true;

        private void RestoreComposerDraft(string sessionId)
        {
            string draft = _composerDrafts.Restore(sessionId);
            if (ChatInputTextBox is not null
                && !string.Equals(ChatInputTextBox.Text, draft, StringComparison.Ordinal))
            {
                ChatInputTextBox.Text = draft;
            }
        }

        private void ClearComposerDraft(string sessionId) => _composerDrafts.Clear(sessionId);

        private void EnsureNewMessagesPillWired()
        {
            if (_newMessagesPillWired)
            {
                return;
            }

            var scroller = ChatScrollViewer;
            if (scroller is null)
            {
                return;
            }

            _newMessagesPillWired = true;
            scroller.ViewChanged += (_, _) => OnChatScrollViewChanged();
        }

        private void OnChatScrollViewChanged()
        {
            var scroller = ChatScrollViewer;
            if (scroller is null || NewMessagesPill is null)
            {
                return;
            }

            double maxOffset = scroller.ScrollableHeight;
            _followOutput = maxOffset <= 0
                || maxOffset - scroller.VerticalOffset <= Services.UiTiming.ChatFollowThresholdPx;
            if (_followOutput)
            {
                NewMessagesPill.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowNewMessagesPill()
        {
            if (_followOutput || NewMessagesPill is null)
            {
                return;
            }

            NewMessagesPill.Visibility = Visibility.Visible;
        }

        private void OnNewMessagesPillClick(object sender, RoutedEventArgs e)
        {
            _followOutput = true;
            if (NewMessagesPill is not null)
            {
                NewMessagesPill.Visibility = Visibility.Collapsed;
            }

            ChatScrollViewer?.ChangeView(null, ChatScrollViewer?.ScrollableHeight, null, disableAnimation: true);
            ChatInputTextBox?.Focus(FocusState.Programmatic);
        }

        private void JumpToLatestForNewTurn()
        {
            _followOutput = true;
            if (NewMessagesPill is not null)
            {
                NewMessagesPill.Visibility = Visibility.Collapsed;
            }
        }


        private async Task StopActiveAgentAsync(bool force = false)
        {
            if (force)
            {
                // Session replacement must not be blocked by another UI action
                // already in flight; the old run still has to be closed first.
                await (_agentController?.CancelAsync() ?? Task.CompletedTask);
            }
            else
            {
                await RunAgentActionAsync(
                    () => _agentController?.CancelAsync() ?? Task.CompletedTask,
                    "Stopping Coding Agent…");
            }
            // Stopping is an interrupt: queued turns stay put until the user asks for
            // them, instead of a new run starting underneath the click that meant stop.
            if (!_queuedTurns.IsEmpty)
            {
                _queuedTurns.HoldAfterStop();
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
            // One branch per decision: approval picks the session or the single
            // action, denial declines. A nested ternary hid that shape.
            Func<Task> action = () => Task.CompletedTask;
            if (_agentController is not null)
            {
                if (!e.Approved)
                {
                    action = () => _agentController.DenyAsync();
                }
                else if (string.Equals(e.Scope, "session", StringComparison.Ordinal))
                {
                    action = () => _agentController.ApproveSessionDirectoryAsync();
                }
                else
                {
                    action = () => _agentController.ApproveAsync();
                }
            }

            await RunAgentActionAsync(
                action,
                e.Approved ? "Submitting approval…" : "Denying approval…");
        }

        private async Task RunAgentActionAsync(Func<Task> action, string status)
        {
            if (_agentActionPending || _agentController is null)
            {
                return;
            }

            _agentActionPending = true;
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
                _agentActionPending = false;
                UpdateAgentInlineState();
            }
        }

        /// <summary>Overrides the workspace for the next runs; null restores the app root default.</summary>
        public void SetRequestedWorkspace(string? workspacePath)
        {
            _requestedWorkspace = string.IsNullOrWhiteSpace(workspacePath) ? null : workspacePath.Trim();
        }

        public async Task OpenSessionAsync(string sessionId, string title)       {
            try
            {
                if (_apiService == null) return;
                // A workspace is single-owner while a run is non-terminal. Wait for
                // the cancellation commit before switching sessions; fire-and-forget
                // cancellation races the next create request and surfaces as the
                // misleading "workspace busy" error.
                await StopActiveAgentAsync(force: true);

                // This attempt takes ownership of the transcript from here. Both halves of
                // the draft move key off the active session id, so the outgoing draft is
                // filed under the outgoing session first and the switch comes after it.
                _composerDrafts.Save(_activeSessionId, ChatInputTextBox?.Text);
                _activeSessionId = sessionId;
                _sessionOpenGenerationSessionId = sessionId;
                // The generation bump is what tells a load still in flight for the previous
                // session that it has been superseded, so it stops instead of painting its
                // history under this session's title. The open token stops the superseded
                // load's I/O as well, instead of only discarding its paint.
                var previousOpenCts = _sessionOpenCts;
                _sessionOpenCts = new CancellationTokenSource();
                previousOpenCts?.Cancel();
                previousOpenCts?.Dispose();
                var generation = ++_sessionOpenGeneration;
                var openToken = _sessionOpenCts.Token;
                _activeSessionTitle = string.IsNullOrWhiteSpace(title) ? "New Chat" : title;
                ResetAgentState();
                JumpToLatestForNewTurn();
                RestoreComposerDraft(sessionId);
                if (AgentPresetBox is not null && AgentPresetBox.SelectedIndex != 0)
                {
                    AgentPresetBox.SelectedIndex = 0;
                }
                SessionOpened?.Invoke(this, new SessionOpenedEventArgs(sessionId, title));
                DetachAgentActivitiesFromMessages();
                Messages.Clear();
                // Every await below is a chance for another switch to take over. Opening a
                // session out of order used to paint its history under the newer session's
                // title, and the backend's active session (activation is a POST) could end
                // up on a different session than the one on screen.
                var history = await _apiService.GetSessionMessagesAsync(sessionId);
                if (!IsCurrentOpen(generation)) return;
                // Durable replay: session messages persist only role/content, so a session
                // opened fresh would show a transcript of prose with every tool row missing.
                // Rebuild the settled rows from each terminal run's durable events. This is
                // what makes the activity feed survive leaving the session or closing the app;
                // without it the rows exist only as transient frames and are gone the moment
                // the live connection ends.
                var replayedRuns = await LoadReplayedRunActivitiesAsync(sessionId, openToken);
                if (!IsCurrentOpen(generation)) return;

                if (history != null)
                {
                    // Which run backs which bubble is decided in one shared place, so this
                    // path and the end-of-turn reload cannot drift apart. Rendering and
                    // backing are separate answers: a bubble can render with no run behind
                    // it, and a hidden envelope must consume neither a bubble nor a run.
                    var placements = ReloadActivitiesPolicy.PlaceReplayedRuns(
                        replayedRuns.Count,
                        history
                            .Select(entry => ReloadActivitiesPolicy.RendersAsAssistant(
                                entry.Role,
                                entry.Content,
                                candidate => ModelToolSelectionParser.LooksLikeToolSelection(
                                    StripDisplayMarkers(candidate))))
                            .ToArray());

                    for (int index = 0; index < history.Count; index++)
                    {
                        var (role, content) = history[index];
                        var placement = placements[index];
                        if (string.Equals(role, "assistant", StringComparison.Ordinal)
                            && !placement.Renders)
                        {
                            continue;
                        }

                        var item = new ChatMessageItem
                        {
                            Role = role,
                            Content = role == "assistant" ? StripDisplayMarkers(content) : content
                        };

                        if (placement.ReplayIndex >= 0
                            && replayedRuns[placement.ReplayIndex].Count > 0)
                        {
                            item.AgentActivities = new ObservableCollection<AgentActivityItem>(
                                replayedRuns[placement.ReplayIndex]);
                            // Register by id so live frames for a still-running run
                            // update these rows in place instead of appending
                            // duplicates beside them, same as the reopen path above.
                            foreach (var replayed in item.AgentActivities)
                            {
                                _agentActivityById.TryAdd(replayed.ActivityId, replayed);
                            }
                        }

                        Messages.Add(item);
                    }

                    if (history.Count > 0)
                    {
                        ScrollChatToBottom();
                    }
                }

                // Usage lives only in RAM during a turn, so an opened session
                // has no provider report and the meter hides its system-plus-
                // tools row. Replay the newest durable report onto the last
                // assistant bubble; the forced repaint below picks it up.
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
            }
            catch (OperationCanceledException)
            {
                // A newer open superseded this load: its token cancelled the I/O
                // on purpose, and the newer session owns the screen.
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"Error in OpenSessionAsync: {ex.Message}");
                Messages.Add(new ChatMessageItem
                {
                    Role = "assistant",
                    Content = "**SESSION LOAD ERROR:** Could not load chat session. Please check backend and try again.",
                    IsError = true
                });
            }
            finally
            {
                // The clear above repaints zero while the refills arrive inside
                // the throttle window, so repaint by force with history in place.
                UpdateContextUsage(force: true);
            }
        }

        /// <summary>
        /// True while the attempt that started as <paramref name="generation"/> is still the
        /// one whose session is open. A superseded attempt must not touch the transcript.
        /// </summary>
        private bool IsCurrentOpen(long generation) =>
            _sessionOpenGeneration == generation
            && string.Equals(_activeSessionId, _sessionOpenGenerationSessionId, StringComparison.Ordinal);

        /// <summary>
        /// Makes <paramref name="sessionId"/> the active session and takes the transcript away
        /// from any open attempt still in flight. Session-creating paths go through here so a
        /// load that started for the previous session can always tell it has been superseded.
        /// </summary>
        private void ActivateSession(string sessionId)
        {
            _activeSessionId = sessionId;
            _sessionOpenGenerationSessionId = sessionId;
            _sessionOpenGeneration++;
        }

        private void ApplyInputWidth()
        {
            if (RootGrid == null || RootGrid.ActualWidth <= 0) return;

            double horizontalInset = RootGrid.ActualWidth < NarrowWidthThreshold
                ? NarrowHorizontalInset
                : RootGrid.ActualWidth < MediumWidthThreshold
                    ? MediumHorizontalInset
                    : WideHorizontalInset;
            double availableWidth = Math.Max(MinAvailableInputWidth, RootGrid.ActualWidth - horizontalInset);
            double targetWidth = Math.Min(ReadingColumnMaxWidth, availableWidth);

            if (WelcomeHeroPanel != null)
            {
                WelcomeHeroPanel.Width = targetWidth;
            }

            if (InputContainer != null)
            {
                InputContainer.Width = targetWidth;
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
                ChatListView.ActualWidth - ChatListView.Padding.Left - ChatListView.Padding.Right);
            column.Width = Math.Min(MessageColumnMaxWidth, viewport);
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

        private async void OnForkFromMessageClick(object sender, RoutedEventArgs e)
        {
            if (_apiService == null) return;
            if (sender is FrameworkElement element && element.Tag is ChatMessageItem msg)
            {
                int index = Messages.IndexOf(msg);
                if (index < 0) return;

                try
                {
                    var branchTitle = $"{_activeSessionTitle} (branch @ {index + 1})";
                    var forked = await _apiService.ForkSessionAsync(_activeSessionId, index, branchTitle);
                    if (forked != null)
                    {
                        SessionForked?.Invoke(this, forked);
                        await OpenSessionAsync(forked.Id, forked.Title);
                    }
                }
                catch (Exception ex)
                {
                    GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Error forking session: {ex.Message}");
                    ShowAgentInlineStatus($"Could not fork the session: {ex.Message}");
                }
            }
        }

        private async void OnRewindToMessageClick(object sender, RoutedEventArgs e)
        {
            if (_apiService == null) return;
            if (sender is FrameworkElement element && element.Tag is ChatMessageItem msg)
            {
                int index = Messages.IndexOf(msg);
                if (index < 0) return;

                try
                {
                    var ok = await _apiService.RewindSessionAsync(_activeSessionId, index);
                    if (ok)
                    {
                        while (Messages.Count > index + 1)
                        {
                            Messages.RemoveAt(Messages.Count - 1);
                        }
                        ScrollChatToBottom();
                    }
                }
                catch (Exception ex)
                {
                    GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Error rewinding session: {ex.Message}");
                    ShowAgentInlineStatus($"Could not rewind the session: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            _agentMonitorCts?.Cancel();
            _agentMonitorCts?.Dispose();
            _agentMonitorCts = null;
            _sessionOpenCts?.Cancel();
            _sessionOpenCts?.Dispose();
            _sessionOpenCts = null;
            if (_contextMeterTimer is not null)
            {
                _contextMeterTimer.Tick -= OnContextMeterTimerTick;
                _contextMeterTimer.Stop();
                _contextMeterTimer = null;
            }
            if (_modelSearchDebounce is not null)
            {
                _modelSearchDebounce.Tick -= OnModelSearchDebounceTick;
                _modelSearchDebounce.Stop();
                _modelSearchDebounce = null;
            }
            _contextMeterScheduled = false;
            TranscriptTypography.Instance.PropertyChanged -= OnTranscriptTypographyChanged;
            _queuedTurns.Items.CollectionChanged -= OnQueuedTurnsChanged;
            Messages.CollectionChanged -= OnMessagesChanged;
            RootGrid.SizeChanged -= OnRootGridSizeChanged;
            ChatListView.ContainerContentChanging -= OnChatContainerContentChanging;
            this.Loaded -= OnChatViewLoaded;
            if (_modelSaveFailureTimer != null)
            {
                _modelSaveFailureTimer.Stop();
                _modelSaveFailureTimer.Tick -= OnModelSaveFailureTimerTick;
                _modelSaveFailureTimer = null;
            }
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
                grouped.StateChanged -= OnGroupedStateChanged;
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
            }
            StopAllTextSweeps();
            DetachWaveTimer();
            DetachContextHoverTimer();
            ClearAgentActivities();
            if (_activityFlushTimer != null)
            {
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
