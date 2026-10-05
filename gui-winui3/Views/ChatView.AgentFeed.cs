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
        private void OnAgentControllerChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                UpdateAgentInlineState();
                if (e.PropertyName == nameof(AgentRunController.CurrentRun))
                {
                    UpdateContextUsage(force: true);
                }
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateAgentInlineState();
                if (e.PropertyName == nameof(AgentRunController.CurrentRun))
                {
                    UpdateContextUsage(force: true);
                }
            });
        }

        private readonly List<AgentActivityModel> _pendingAgentActivities = new();
        private DispatcherQueueTimer? _activityFlushTimer;

        private void OnAgentActivityReceived(object? sender, AgentActivityModel activity)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                EnqueueAgentActivity(activity);
                return;
            }

            if (!DispatcherQueue.TryEnqueue(() => EnqueueAgentActivity(activity)))
            {
                GuiDiagnosticWriter.AppendTraceLine("[ChatView] Dropped activity frame: dispatcher queue rejected enqueue.");
            }
        }

        private void EnqueueAgentActivity(AgentActivityModel activity)
        {
            // An activity frame may arrive while the turn is armed but the
            // controller has not flipped busy yet (the Thinking Started frame
            // is published before the first model call completes): arm the
            // assistant anchor now so the first frames are never dropped.
            EnsureAgentActivityAnchor();
            _pendingAgentActivities.Add(activity);
            if (_pendingAgentActivities.Count == 1 && _activityFlushTimer?.IsRunning != true)
            {
                // Lone frame: render on the next dispatcher turn instead of
                // waiting out the batch window. Burst traffic still falls
                // through to the timer below and renders as one batch.
                if (DispatcherQueue.TryEnqueue(FlushAgentActivities))
                {
                    return;
                }
            }

            if (_activityFlushTimer is null)
            {
                _activityFlushTimer = DispatcherQueue.CreateTimer();
                _activityFlushTimer.Interval = Services.UiTiming.ActivityFlushBatch;
                _activityFlushTimer.Tick += OnActivityFlushTick;
            }

            if (!_activityFlushTimer.IsRunning)
            {
                _activityFlushTimer.Start();
            }
        }

        private void OnActivityFlushTick(Microsoft.UI.Dispatching.DispatcherQueueTimer? timer, object? _)
        {
            FlushAgentActivities();
        }

        private void FlushAgentActivities()
        {
            _activityFlushTimer?.Stop();
            if (_pendingAgentActivities.Count == 0)
            {
                return;
            }

            // Drain to a snapshot so re-entrant activity events during Apply
            // cannot mutate the collection mid-flush.
            var batch = _pendingAgentActivities.ToArray();
            _pendingAgentActivities.Clear();
            foreach (var activity in batch)
            {
                ApplyAgentActivity(activity);
            }

            AgentActivityGrouper.Reconcile(AgentActivities);
            ScrollChatToBottom();
        }

        /// <summary>
        /// The activity flush timer can lose the race with the run's terminal
        /// transition, and frames are ignored once the controller is idle: the
        /// last frame of a run would be dropped and a running row (for example
        /// "Thinking") would keep its progress affordance forever. Drain the
        /// queue without the idle gate, then settle anything still running.
        /// </summary>
        private void SettleAgentActivitiesAfterRun()
        {
            if (_pendingAgentActivities.Count > 0)
            {
                var queued = _pendingAgentActivities.ToArray();
                _pendingAgentActivities.Clear();
                foreach (var activity in queued)
                {
                    ApplyAgentActivity(activity, allowWhenIdle: true);
                }
            }

            AgentActivityGrouper.SettleRunningRows(AgentActivities);
            AgentActivityGrouper.Reconcile(AgentActivities);
            // A block that never recorded its completion cannot keep claiming
            // "running" once the run has ended.
            TerminalPanelControl.SettleRunningBlocks();
        }

        private void ApplyAgentActivity(AgentActivityModel activity, bool allowWhenIdle = false)
        {
            if ((!allowWhenIdle && !HasRunInHand())
                || string.IsNullOrWhiteSpace(activity.ActivityId))
            {
                return;
            }

            if (_agentActivityById.TryGetValue(activity.ActivityId, out var existing))
            {
                // Every frame is applied as it arrives. A completion used to be held
                // for a minimum window to hide the flicker of a short row, which read
                // as the feed standing still and then jumping: the row kept its
                // running affordance while the tool had already finished.
                if (existing.UpdateFrom(activity))
                {
                    TrackTerminalToolState(existing);
                    TrackTerminalOutputRow(existing);
                }
            }
            else
            {
                var item = new AgentActivityItem(activity);

                _agentActivityById.Add(item.ActivityId, item);
                // Arrival order is the harness's order, and the harness emits one
                // Thinking activity per model step keyed by that step's id, published
                // when the step starts — before any of the step's tools. So a Thinking
                // row can only ever arrive after a tool row when it belongs to a *later*
                // step, and it belongs at the bottom.
                //
                // It used to be walked backwards to just below the previous Thinking row
                // "so a settled Thinking row stays above the tool rows that followed it".
                // Nothing could benefit from that: the step's own row is always inserted
                // first, so the walk-back never fired for the case it was written for. It
                // only fired for a new step, where it put that step's failure above the
                // previous step's work — a run failing on step 2 read as though it had
                // failed before the tool it ran.
                AgentActivities.Add(item);
                FreezeTextSegmentForActivity(item);
                TrackTerminalToolState(item);
                TrackTerminalOutputRow(item);
            }
        }

        /// <summary>
        /// Remembers which terminal row is the one the output stream belongs to.
        /// </summary>
        /// <remarks>
        /// One command runs at a time, so the row still in the Started state is the row the
        /// next output chunk belongs to; a row that settled stops accepting chunks and keeps
        /// what it already captured. Matching by tool call id instead would need the feed to
        /// know how the terminal panel names its blocks, which is the panel's business.
        /// </remarks>
        private AgentActivityItem? _runningTerminalItem;

        /// <summary>
        /// The panel block the running command streams into, bound on its first
        /// output chunk. Completion settles the block by this id, never by label:
        /// "Running X" and "Ran X" are different strings, and settling the newest
        /// unfinished block instead would land one command's outcome on another.
        /// </summary>
        private string? _runningTerminalExecutionId;

        private void TrackTerminalOutputRow(AgentActivityItem item)
        {
            if (item.Kind != RuAgent.Protocol.Harness.AgentActivityKind.Terminal)
            {
                return;
            }

            if (item.IsRunning)
            {
                _runningTerminalItem = item;
            }
            else if (ReferenceEquals(_runningTerminalItem, item))
            {
                _runningTerminalItem = null;
            }
        }

        /// <summary>
        /// Arms the live assistant bubble for activity-only progress while the
        /// turn is still being sent: SendCurrentMessageAsync holds
        /// _isSendingUserTurn across the whole create-run await, so the first
        /// SSE frames (Thinking Started) land before the controller flips busy
        /// or the first text delta arrives. A no-op once the anchor exists.
        /// </summary>
        private void EnsureAgentActivityAnchor()
        {
            if (_activeAgentAssistantMessage is not null || !_isSendingUserTurn)
            {
                return;
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

        /// <summary>
        /// Freezes the prose that arrived before a new activity into a settled
        /// text segment, so the feed stays chronological: narration above, the
        /// tool row below it, fresh prose in a new live tail. Only touches the
        /// active message while it still shares the live activity collection.
        /// </summary>
        private void FreezeTextSegmentForActivity(AgentActivityItem item)
        {
            var message = _activeAgentAssistantMessage;
            if (message is null || !ReferenceEquals(message.AgentActivities, AgentActivities))
            {
                return;
            }

            var content = message.Content ?? string.Empty;
            var frozen = Math.Min(_segmentFrozenLength, content.Length);
            // Freeze on a line edge so a mid-sentence activity frame does not
            // slice one sentence across two bubbles. The rest keeps streaming
            // in the tail below the new row.
            var splitAt = AgentSegmentSplitter.SnapToLineStart(content, frozen);
            var segments = message.Segments;
            if (segments is null)
            {
                segments = new ObservableCollection<AssistantSegment>();
                message.Segments = segments;
            }

            if (segments.Count > 0 && segments[^1] is TextSegment tail)
            {
                tail.Text = content.Substring(frozen, splitAt - frozen);
                tail.IsError = message.IsError;
            }
            else if (splitAt > frozen)
            {
                segments.Add(new TextSegment(content.Substring(frozen, splitAt - frozen)) { IsError = message.IsError });
            }

            _activitySplitOffsetById[item.ActivityId] = splitAt;
            _segmentFrozenLength = splitAt;
            segments.Add(new ActivitySegment(item.ActivityId, item));
            segments.Add(new TextSegment(string.Empty) { IsError = message.IsError });
        }

        private void UpdateLiveTailText(ChatMessageItem? message)
        {
            if (message?.Segments is { Count: > 0 } segments
                && segments[^1] is TextSegment tail)
            {
                var content = message.Content ?? string.Empty;
                var frozen = Math.Min(_segmentFrozenLength, content.Length);
                tail.Text = content.Substring(frozen);
            }
        }

        private List<ActivitySplitOffset> TakeSplitOffsetSnapshot(IEnumerable<AgentActivityItem> items)
        {
            var snapshot = new List<ActivitySplitOffset>();
            foreach (var item in items)
            {
                if (item != null && _activitySplitOffsetById.TryGetValue(item.ActivityId, out var offset))
                {
                    snapshot.Add(new ActivitySplitOffset(item.ActivityId, offset));
                }
            }

            return snapshot;
        }

        private static void SyncSegmentErrorState(ChatMessageItem message)
        {
            if (message.Segments is null)
            {
                return;
            }

            foreach (var segment in message.Segments)
            {
                if (segment is TextSegment text)
                {
                    text.IsError = message.IsError;
                }
            }
        }

        private static void RebuildMessageSegments(ChatMessageItem message)
        {
            if (message.AgentActivities is { Count: > 0 } activities
                && message.ActivitySplitOffsets != null
                && !string.IsNullOrEmpty(message.Content))
            {
                message.Segments = new ObservableCollection<AssistantSegment>(
                    ActivitySegmenter.BuildSettled(message.Content, activities, message.ActivitySplitOffsets));
                SyncSegmentErrorState(message);
            }
            else
            {
                message.Segments = null;
            }
        }

        private void TrackTerminalToolState(AgentActivityItem item)
        {
            if (item.Kind != RuAgent.Protocol.Harness.AgentActivityKind.Terminal)
            {
                return;
            }

            if (item.State == RuAgent.Protocol.Harness.AgentActivityState.Started)
            {
                TerminalPanelControl.NoteToolLabel(item.DisplayText);
                _runningTerminalExecutionId = null;
                return;
            }

            if (item.State is RuAgent.Protocol.Harness.AgentActivityState.Completed
                or RuAgent.Protocol.Harness.AgentActivityState.Failed)
            {
                CompleteTerminalBlock(
                    _runningTerminalExecutionId,
                    failed: item.State == RuAgent.Protocol.Harness.AgentActivityState.Failed);
            }
        }

        private void OnAgentSummaryReceived(object? sender, AgentRunSummaryModel summary)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                ApplyAgentRunSummary(summary);
                return;
            }

            DispatcherQueue.TryEnqueue(() => ApplyAgentRunSummary(summary));
        }

        private void ApplyAgentRunSummary(AgentRunSummaryModel summary)
        {
            if (!AgentRunActivityPolicy.Evaluate(SnapshotRunActivity()).IsSessionBusy)
            {
                return;
            }

            var item = new AgentRunSummaryItem(summary);
            if (item.IsFailure && HasRenderedAgentReply())
            {
                // A transient assistant reply may already be visible when the
                // history/outbox finalization reports a late failure. The reply
                // is the user-visible result of this turn; do not add noise
                // after it.
                return;
            }

            // The terminal summary adds nothing to the transcript. The feed already
            // said what happened: a run that fails on a model step leaves a settled
            // row reading "Failed: <reason>" in the same list as the work that
            // preceded it. The card repeated that as a separate red block under the
            // transcript, and repeated it again after a reload.
            //
            // Two copies of one fact is worse than one: the second arrives lower on
            // the screen than the work it explains, so it reads as a new event rather
            // than as the verdict on the rows above it. Success and cancelled runs
            // already added nothing here, so a failure no longer needs a card either —
            // the two outcomes are now treated alike, which they always were in the
            // run's own accounting.
        }

        private void OnAgentAssistantDeltaReceived(object? sender, AgentAssistantDeltaModel delta)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => OnAgentAssistantDeltaReceived(sender, delta));
                return;
            }

            AgentRunModel? run = _agentController?.CurrentRun;
            if (run is null || !HasRunInHand())
            {
                return;
            }

            ChatMessageItem? activeAssistantMessage;
            string? activeRunId;
            lock (_accumulatorLock)
            {
                activeAssistantMessage = _activeAgentAssistantMessage;
                activeRunId = _activeAgentRunId;
            }
            if (activeAssistantMessage is null)
            {
                EnsureAgentActivityAnchor();
                lock (_accumulatorLock)
                {
                    activeAssistantMessage = _activeAgentAssistantMessage;
                }
                if (activeAssistantMessage is null)
                {
                    return;
                }
            }

            activeRunId ??= run.RunId;
            lock (_accumulatorLock)
            {
                _activeAgentRunId = activeRunId;
                _replyAccumulator.Rebind(_activeSessionId, activeRunId);
            }
            if (!string.Equals(activeRunId, run.RunId, StringComparison.Ordinal))
            {
                return;
            }

            string key = $"{run.RunId}:{delta.ModelStepId}:{delta.TransientSequence}";
            if (!_agentDeltaKeys.Add(key))
            {
                return;
            }

            // Use accumulator as single authority for dedup + text; also keep legacy set for now
            bool accepted = delta.IsReset
                ? _replyAccumulator.TryReset(run.RunId, delta.ModelStepId, delta.TransientSequence)
                : _replyAccumulator.TryAppend(run.RunId, delta.ModelStepId, delta.TransientSequence, delta.Text);
            if (!accepted)
            {
                // If no active accumulator (e.g., after Invalidate), begin with real runId and retry once
                if (!_replyAccumulator.HasSession
                    || !string.Equals(_replyAccumulator.SessionId, _activeSessionId, StringComparison.Ordinal))
                {
                    return;
                }

                _replyAccumulator.Rebind(_activeSessionId, run.RunId);
                lock (_accumulatorLock)
                {
                    _activeAgentRunId = run.RunId;
                }
                accepted = delta.IsReset
                    ? _replyAccumulator.TryReset(run.RunId, delta.ModelStepId, delta.TransientSequence)
                    : _replyAccumulator.TryAppend(run.RunId, delta.ModelStepId, delta.TransientSequence, delta.Text);
                if (!accepted)
                {
                    return;
                }
            }

            _activeAgentReplyReceived = true;
            _replyRenderScheduler.Schedule();
        }

        private void OnAgentUsageReceived(object? sender, AgentUsageReceivedEventArgs usage)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => OnAgentUsageReceived(sender, usage));
                return;
            }

            if (_agentController?.CurrentRun is not { } run
                || !string.Equals(run.RunId, usage.RunId, StringComparison.Ordinal)
                || !string.Equals(run.SessionId, usage.SessionId, StringComparison.Ordinal))
            {
                return;
            }

            _retainedContextMeter.ObserveUsage(
                usage.RunId,
                usage.SessionId,
                usage.Sequence,
                run.ProviderProtocol,
                usage.Usage);

            _activeAgentUsage = usage.Usage;
            if (_activeAgentAssistantMessage is not null)
            {
                _activeAgentAssistantMessage.Usage = usage.Usage;
            }
            else
            {
                // The turn already completed and history was reloaded before the
                // usage payload arrived. Patch the last assistant bubble that was
                // rendered without usage so the footer does not go missing.
                for (int index = Messages.Count - 1; index >= 0; index--)
                {
                    var item = Messages[index];
                    if (string.Equals(item.Role, "assistant", StringComparison.Ordinal)
                        && item.Usage is null)
                    {
                        item.Usage = usage.Usage;
                        break;
                    }
                }
            }
            UpdateContextUsage();
        }

        private void OnAgentContextCompacted(object? sender, AgentRunContextCompactedEventArgs compacted)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => OnAgentContextCompacted(sender, compacted));
                return;
            }

            if (_agentController?.CurrentRun is not { } run
                || !string.Equals(run.RunId, compacted.RunId, StringComparison.Ordinal)
                || !string.Equals(run.SessionId, compacted.SessionId, StringComparison.Ordinal)
                || !string.Equals(run.SessionId, _activeSessionId, StringComparison.Ordinal))
            {
                return;
            }

            if (compacted.Sequence is not { } sequence)
            {
                return;
            }

            _retainedContextMeter.ScopeToRun(run, _activeSessionId);
            _retainedContextMeter.ObserveCompaction(run.RunId, run.SessionId, sequence, Messages.Count);
            UpdateContextUsage(force: true);
        }

        private void OnAgentRunAdopted(object? sender, AgentRunAdoptedEventArgs adopted)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => OnAgentRunAdopted(sender, adopted));
                return;
            }

            if (!string.Equals(adopted.Run.SessionId, _activeSessionId, StringComparison.Ordinal))
            {
                return;
            }

            _retainedContextMeter.ScopeToRun(adopted.Run, _activeSessionId);
            _retainedContextMeter.MarkHistoryBoundaryUnknown(
                adopted.Run.RunId,
                adopted.Run.SessionId,
                adopted.Run.LastSequence);
            UpdateContextUsage(force: true);
        }

        private void ApplyReplySnapshot(AgentReplySnapshot snapshot)
        {
            if (_activeAgentAssistantMessage == null
                || !string.Equals(snapshot.SessionId, _activeSessionId, StringComparison.Ordinal)
                || !string.Equals(snapshot.RunId, _activeAgentRunId, StringComparison.Ordinal))
            {
                return;
            }

            if (!Messages.Contains(_activeAgentAssistantMessage))
            {
                Messages.Add(_activeAgentAssistantMessage);
            }

            // Non-text deltas still schedule a render: skip the layout + scroll
            // pass when the text did not actually grow.
            if (!string.Equals(_activeAgentAssistantMessage.Content, snapshot.Text, StringComparison.Ordinal))
            {
                _activeAgentAssistantMessage.Content = snapshot.Text;
                UpdateLiveTailText(_activeAgentAssistantMessage);
                ScrollChatToBottom();
            }

            _activeAgentAssistantMessage.IsStreaming = true;
            UpdateContextUsage();
        }

        private void ClearAgentActivities()
        {
            StopAllTextSweeps();
            _activityFlushTimer?.Stop();
            _pendingAgentActivities.Clear();
            _agentActivityById.Clear();
            _activitySplitOffsetById.Clear();
            _segmentFrozenLength = 0;
            AgentActivities.Clear();
            _agentBusySeen = false;
        }

        private void DetachAgentActivitiesFromMessages()
        {
            foreach (var message in Messages)
            {
                if (ReferenceEquals(message.AgentActivities, AgentActivities))
                {
                    message.AgentActivities = null;
                    message.Segments = null;
                }
            }
        }

        private void StartNewAgentActivityRun()
        {
            foreach (var message in Messages)
            {
                if (ReferenceEquals(message.AgentActivities, AgentActivities))
                {
                    message.AgentActivities = AgentActivities.Count > 0
                        ? new ObservableCollection<AgentActivityItem>(AgentActivities)
                        : null;
                    message.ActivitySplitOffsets = message.AgentActivities != null
                        ? TakeSplitOffsetSnapshot(message.AgentActivities)
                        : null;
                }
            }
            ClearAgentActivities();
            ClearTerminalBlocks();
        }

        // ── Terminal output panel ──────────────────────────────
        // The block list, its bound, the label carried onto the next block, and the
        // show/hide state all live in TerminalPanelControl. The view only forwards.
        private void OnAgentToolOutputReceived(object? sender, AgentToolOutputModel output)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                ApplyAgentToolOutput(output);
                return;
            }

            DispatcherQueue.TryEnqueue(() => ApplyAgentToolOutput(output));
        }

        private void ApplyAgentToolOutput(AgentToolOutputModel output)
        {
            if (!AgentRunActivityPolicy.Evaluate(SnapshotRunActivity()).IsSessionBusy)
            {
                return;
            }

            TerminalPanelControl.AppendOutput(output.ExecutionId, output.Text);
            _runningTerminalExecutionId ??= output.ExecutionId;

            // The feed row for the command being run reads the same stream, so the reader
            // can see what a command printed without leaving the transcript. A row that
            // nobody is running anymore keeps what it already received.
            _runningTerminalItem?.AppendTerminalOutput(output.Text);
        }

        private void CompleteTerminalBlock(string? executionId, bool failed)
        {
            TerminalPanelControl.CompleteBlock(executionId, failed);
            _runningTerminalExecutionId = null;
        }

        private void ClearTerminalBlocks()
        {
            TerminalPanelControl.Clear();
            _runningTerminalItem = null;
            _runningTerminalExecutionId = null;
        }

        // ── Activity text wave animation ──────────────────────────────
        private readonly Dictionary<TextBlock, AgentActivityItem> _activeWaveTextBlocks = new();
        private DispatcherQueueTimer? _waveTimer;
        private readonly System.Diagnostics.Stopwatch _waveStopwatch = new();

        private void OnActivityItemLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid grid) return;
            grid.DataContextChanged -= OnActivityItemDataContextChanged;
            grid.DataContextChanged += OnActivityItemDataContextChanged;
            AttachActivityItem(grid);
        }

        private void OnActivityItemDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (sender is Grid grid)
            {
                AttachActivityItem(grid);
            }
        }

        private void OnActivityItemUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid grid) return;
            grid.DataContextChanged -= OnActivityItemDataContextChanged;
            DetachActivityItem(grid);
        }

        /// <summary>
        /// The template element carrying the row's label, by row shape.
        /// </summary>
        /// <remarks>
        /// The sweep and the running-to-settled style updates target this one
        /// element. It used to be found by grid column, which broke silently
        /// when the header was restructured (waiting pill removed, labels moved
        /// into toggles): no column held a label anymore, the lookup returned
        /// null, and every Thinking row went static with no error anywhere.
        /// Names are pinned by contract test, so the template and this lookup
        /// cannot drift apart again without a test going red.
        /// </remarks>
        internal static string ActivityLabelElementName(AgentActivityItem item) =>
            item.HasThinkingDetail ? "ThinkingLabel"
            : item.ShowRowDetailToggle ? "EditPreviewLabel"
            : "AgentActivityText";

        private void AttachActivityItem(Grid grid)
        {
            DetachActivityItem(grid, clearEntrance: false);

            if (grid.DataContext is not AgentActivityItem item)
                return;

            if (grid.Tag == null && Services.UiTiming.AnimationsEnabled)
            {
                grid.Opacity = 0;
                var translate = new TranslateTransform { Y = 4 };
                grid.RenderTransform = translate;

                var fadeIn = new DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(fadeIn, grid);
                Storyboard.SetTargetProperty(fadeIn, "Opacity");

                var slideIn = new DoubleAnimation
                {
                    From = 4.0,
                    To = 0.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(slideIn, translate);
                Storyboard.SetTargetProperty(slideIn, "Y");

                var entranceSb = new Storyboard();
                entranceSb.Children.Add(fadeIn);
                entranceSb.Children.Add(slideIn);
                entranceSb.Begin();
            }

            TextBlock? displayText = grid.FindName(ActivityLabelElementName(item)) as TextBlock;
            if (displayText == null) return;

            ApplyActivityTextStyle(displayText, item);
            ApplyActivityLiveSetting(grid, item);
            if (item.Todos is { Count: > 0 } latestTodos)
            {
                DispatcherQueue?.TryEnqueue(() => UpdateTodoPanel(latestTodos));
            }

            PropertyChangedEventHandler handler = (s, args) =>
            {
                if (string.IsNullOrEmpty(args.PropertyName) ||
                    args.PropertyName == nameof(AgentActivityItem.State) ||
                    args.PropertyName == nameof(AgentActivityItem.IsRunning) ||
                    args.PropertyName == nameof(AgentActivityItem.IsCompleted) ||
                    args.PropertyName == nameof(AgentActivityItem.IsFailed) ||
                    args.PropertyName == nameof(AgentActivityItem.IsWaiting))
                {
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        if (s is AgentActivityItem ai && ReferenceEquals(grid.DataContext, ai))
                        {
                            ApplyActivityTextStyle(displayText, ai);
                            ApplyActivityLiveSetting(grid, ai);
                        }
                    });
                }
            };
            item.PropertyChanged += handler;
            grid.Tag = (handler, displayText, item);
        }

        /// <summary>
        /// A waiting row must not whisper: it announces assertively while every
        /// other row stays polite.
        /// </summary>
        private static void ApplyActivityLiveSetting(Grid grid, AgentActivityItem item) =>
            AutomationProperties.SetLiveSetting(
                grid,
                item.IsWaiting
                    ? Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive
                    : Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        /// <summary>
        /// Draws the standing todo panel above the composer from the latest list.
        /// </summary>
        /// <remarks>
        /// One block fused with the composer: the panel owns the top border and
        /// top corners, the input owns the bottom ones. Every activity carrying
        /// todos replaces the previous plan. Passing null or an empty list hides
        /// the panel, and so does a fully completed list: a finished plan has
        /// nothing left to watch. Collapsed shows just the one-line
        /// header with the counts; expanded renders the whole list.
        /// </remarks>
        private void UpdateTodoPanel(IReadOnlyList<TodoItemModel>? todos)
        {
            _latestTodos = todos;
            if (TodoPanel is null || TodoPanelList is null || TodoPanelHeader is null
                || TodoPanelCount is null || TodoPanelProgress is null)
            {
                return;
            }

            if (todos is not { Count: > 0 })
            {
                TodoPanel.Visibility = Visibility.Collapsed;
                RestoreInputBorder();
                return;
            }

            int done = 0;
            string? current = null;
            foreach (var todo in todos)
            {
                if (todo.IsCompleted)
                {
                    done++;
                }
                else if (current is null)
                {
                    current = todo.Content;
                }
            }

            if (done == todos.Count)
            {
                ClearTodoPanel();
                return;
            }

            TodoPanelCount.Text = $"{done}/{todos.Count}";
            TodoPanelHeader.Text = current ?? "All tasks done";
            TodoPanelProgress.Maximum = todos.Count;
            TodoPanelProgress.Value = done;
            string progressName = $"Tasks {done} of {todos.Count} done";
            AutomationProperties.SetName(TodoPanelProgress, progressName);
            ToolTipService.SetToolTip(TodoPanelProgress, progressName);
            if (Application.Current?.Resources.TryGetValue(
                    done == todos.Count ? "AppSuccessBrush" : "RuAccentBrush",
                    out var countColor) == true
                && countColor is Brush countBrush)
            {
                TodoPanelCount.Foreground = countBrush;
            }

            if (TodoPanelToggle is not null)
            {
                AutomationProperties.SetName(
                    TodoPanelToggle,
                    _todoPanelExpanded ? "Collapse task list" : "Expand task list");
            }

            TodoPanel.Visibility = Visibility.Visible;
            FuseInputBorder();

            if (_todoPanelExpanded)
            {
                TodoPanelList.Visibility = Visibility.Visible;
                TodoPanelList.Render(todos);
            }
            else
            {
                TodoPanelList.Visibility = Visibility.Collapsed;
            }
        }

        private void ClearTodoPanel()
        {
            _latestTodos = null;
            _todoPanelExpanded = false;
            if (TodoPanelToggleIcon is not null)
            {
                TodoPanelToggleIcon.Glyph = "\uE70E";
            }

            if (TodoPanel is not null)
            {
                TodoPanel.Visibility = Visibility.Collapsed;
            }

            RestoreInputBorder();
        }

        /// <summary>
        /// Joins the input to the panel above it by squaring its top corners.
        /// The panel carries no outline of its own; the input keeps its
        /// borderless shadow style, so the two read as one block.
        /// </summary>
        private void FuseInputBorder()
        {
            if (InputBorder is null)
            {
                return;
            }

            var card = UiCornerRadius.Card;
            InputBorder.CornerRadius = new CornerRadius(0, 0, card.BottomRight, card.BottomRight);
        }

        /// <summary>
        /// Gives the input its own standalone border back when the panel hides.
        /// </summary>
        private void RestoreInputBorder()
        {
            if (InputBorder is null)
            {
                return;
            }

            InputBorder.BorderThickness = new Thickness(0);
            InputBorder.CornerRadius = UiCornerRadius.Card;
        }

        /// <summary>
        /// Expands or collapses the standing todo panel. Viewing only: the list
        /// itself stays read-only, so this changes no plan state.
        /// </summary>
        private void OnTodoPanelToggleClick(object sender, RoutedEventArgs e)
        {
            _todoPanelExpanded = !_todoPanelExpanded;
            if (TodoPanelToggleIcon is not null)
            {
                TodoPanelToggleIcon.Glyph = _todoPanelExpanded ? "\uE70D" : "\uE70E";
            }

            UpdateTodoPanel(_latestTodos);
        }

        private void DetachActivityItem(Grid grid, bool clearEntrance = true)
        {
            if (grid.Tag is ValueTuple<PropertyChangedEventHandler, TextBlock, AgentActivityItem> tuple)
            {
                tuple.Item3.PropertyChanged -= tuple.Item1;
                StopTextSweep(tuple.Item2);
            }
            if (clearEntrance)
            {
                grid.Tag = null;
            }
        }

        /// <summary>
        /// Copies one terminal row's output. The panel text is already bounded
        /// by the row, so no extra truncation is applied here.
        /// </summary>
        private void OnCopyTerminalOutputClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element
                && element.Tag is string text
                && !string.IsNullOrEmpty(text))
            {
                try
                {
                    var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    package.SetText(text);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                }
                catch (Exception ex)
                {
                    // A clipboard write can fail when another process holds it open,
                    // and an exception out of a click handler takes the app with it.
                    GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Copy terminal output failed: {ex.Message}");
                }
            }
        }

        private void ApplyActivityTextStyle(TextBlock textBlock, AgentActivityItem item)
        {
            // Applied here rather than bound in markup. A binding inside a DataTemplate has
            // to resolve the resource while the template is being parsed, and this one is
            // registered from code, which is what made the app die on the first message.
            textBlock.FontSize = Services.TranscriptTypography.Instance.ActivityFontSize;

            if (item.IsRunning)
            {
                StartTextSweep(textBlock, item);
            }
            else
            {
                StopTextSweep(textBlock);
                var targetBrush = item.IsFailed
                    ? (SolidColorBrush)Application.Current.Resources["RuDangerBrush"]
                    : (SolidColorBrush)Application.Current.Resources["RuInkBrush"];

                textBlock.Foreground = targetBrush;
                // Waiting rows need user action: italic sets them apart without
                // spending a color that would fail AA on the light theme.
                textBlock.FontStyle = item.IsWaiting
                    ? Windows.UI.Text.FontStyle.Italic
                    : Windows.UI.Text.FontStyle.Normal;
            }
        }

        private void StartTextSweep(TextBlock textBlock, AgentActivityItem item)
        {
            // Reduced motion: a static muted tint instead of the 25fps wave.
            if (!Services.UiTiming.AnimationsEnabled)
            {
                StopTextSweep(textBlock);
                if (Application.Current?.Resources.TryGetValue("RuMutedBrush", out var muted) == true
                    && muted is Brush mutedBrush)
                {
                    textBlock.Foreground = mutedBrush;
                }

                return;
            }

            _activeWaveTextBlocks[textBlock] = item;

            if (_waveTimer == null)
            {
                _waveTimer = DispatcherQueue.CreateTimer();
                _waveTimer.Interval = Services.UiTiming.WaveFrame; // 25 FPS
                _waveTimer.Tick += OnWaveTimerTick;
            }

            if (!_waveTimer.IsRunning)
            {
                _waveStopwatch.Restart();
                _waveTimer.Start();
            }

            UpdateWaveTextBlock(textBlock, 0.0, ResolveWaveBaseColor());
        }

        private static Windows.UI.Color ResolveWaveBaseColor()
        {
            var mutedBrush = Application.Current.Resources["RuMutedBrush"] as SolidColorBrush;
            return mutedBrush?.Color ?? Windows.UI.Color.FromArgb(255, 148, 163, 184);
        }

        private void StopTextSweep(TextBlock textBlock)
        {
            if (_activeWaveTextBlocks.Remove(textBlock))
            {
                if (_activeWaveTextBlocks.Count == 0)
                {
                    _waveTimer?.Stop();
                    _waveStopwatch.Reset();
                }
            }
        }

        private void StopAllTextSweeps()
        {
            _activeWaveTextBlocks.Clear();
            _waveTimer?.Stop();
            _waveStopwatch.Reset();
        }

        private void OnWaveTimerTick(DispatcherQueueTimer sender, object args)
        {
            if (_activeWaveTextBlocks.Count == 0)
            {
                sender.Stop();
                _waveStopwatch.Reset();
                return;
            }

            double elapsed = _waveStopwatch.Elapsed.TotalSeconds;
            double phase = (elapsed / Services.UiTiming.ActivityWavePeriodSeconds) % 1.0;

            // One resource lookup per tick, not per block: theme stays live
            // because the color is re-resolved on every tick.
            var baseColor = ResolveWaveBaseColor();

            List<TextBlock>? deadBlocks = null;
            foreach (var kvp in _activeWaveTextBlocks)
            {
                var tb = kvp.Key;
                var owner = kvp.Value;
                if (tb.XamlRoot == null
                    || !owner.IsRunning
                    || !owner.IsVisibleInFeed)
                {
                    deadBlocks ??= new List<TextBlock>();
                    deadBlocks.Add(tb);
                    continue;
                }
                UpdateWaveTextBlock(tb, phase, baseColor);
            }

            if (deadBlocks != null)
            {
                foreach (var dead in deadBlocks)
                {
                    _activeWaveTextBlocks.Remove(dead);
                }
                if (_activeWaveTextBlocks.Count == 0)
                {
                    sender.Stop();
                    _waveStopwatch.Reset();
                }
            }
        }

        private static void UpdateWaveTextBlock(TextBlock tb, double phase, Windows.UI.Color baseColor)
        {
            // Subtle Cyan-Violet Aurora beam center (-0.35 to 1.35)
            double center = -0.35 + 1.70 * phase;

            // Cache brush per TextBlock to avoid allocation churn
            if (tb.Tag is not LinearGradientBrush brush)
            {
                brush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint = new Point(1, 0.5),
                    GradientStops =
                    {
                        new GradientStop { Color = baseColor, Offset = 0.00 },
                        new GradientStop { Color = baseColor, Offset = 0.25 },
                        new GradientStop { Color = baseColor, Offset = 0.50 },
                        new GradientStop { Color = baseColor, Offset = 0.75 },
                        new GradientStop { Color = baseColor, Offset = 1.00 },
                    }
                };
                tb.Tag = brush;
            }

            // Mutate stops in place
            var stops = brush.GradientStops;
            stops[0].Color = SampleAuroraColor(0.00, center, baseColor);
            stops[1].Color = SampleAuroraColor(0.25, center, baseColor);
            stops[2].Color = SampleAuroraColor(0.50, center, baseColor);
            stops[3].Color = SampleAuroraColor(0.75, center, baseColor);
            stops[4].Color = SampleAuroraColor(1.00, center, baseColor);

            tb.Foreground = brush;
        }

        private static Windows.UI.Color SampleAuroraColor(double u, double center, Windows.UI.Color baseColor) =>
            Services.AgentActivityPalette.SampleAuroraColor(u, center, baseColor);

        private static Windows.UI.Color LerpColor(Windows.UI.Color from, Windows.UI.Color to, double progress) =>
            Services.AgentActivityPalette.LerpColor(from, to, progress);
    }
}
