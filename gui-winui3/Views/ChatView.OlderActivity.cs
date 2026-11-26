using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views
{
    /// <summary>
    /// A reopened long run shows its newest activity first. Its older activity loads a page at
    /// a time when the sentinel at the top of the run's first reply scrolls into view, and the
    /// view keeps what the reader was looking at in place while the older rows arrive above it.
    /// </summary>
    public sealed partial class ChatView
    {
        // Set while freshly inserted rows wait for the scroll correction: the new sentinel is
        // briefly on screen at the old offset, and must not start the next page by accident.
        private bool _holdOlderActivitySentinels;

        private void OnOlderActivitySentinelViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
        {
            if (_holdOlderActivitySentinels
                || sender.Tag is not ChatMessageItem { OlderActivity: { IsLoading: false } } message
                || sender.ActualHeight <= 0 || sender.Visibility != Visibility.Visible)
            {
                return;
            }

            // The effective viewport is in the sentinel's own coordinates: it is on screen when
            // that rectangle overlaps the sentinel's bounds.
            var viewport = args.EffectiveViewport;
            if (viewport.Height <= 0 || viewport.Bottom <= 0 || viewport.Top >= sender.ActualHeight
                || viewport.Right <= 0 || viewport.Left >= sender.ActualWidth)
            {
                return;
            }

            _ = LoadOlderActivityAsync(message);
        }

        private async Task LoadOlderActivityAsync(ChatMessageItem message)
        {
            if (_apiService is not { } api || message.OlderActivity is not { IsLoading: false } paging) return;

            var sessionId = _activeSessionId;
            var generation = _sessionOpenGeneration;
            var cancellationToken = _historyOpenCancellation;
            paging.IsLoading = true;
            try
            {
                var page = await api.ListRunActivitiesAsync(paging.RunId, paging.NextBeforeSequence, cancellationToken);
                if (!OwnsSessionHistoryLoad(sessionId, generation, cancellationToken)) return;
                var shown = _historyReplay.FirstOrDefault(run => string.Equals(run.RunId, paging.RunId, StringComparison.Ordinal));
                if (page is null || shown is null)
                {
                    if (page is null) ShowAgentInlineStatus("Could not load earlier activity. Scroll up to try again.");
                    return;
                }

                // The run's bubbles run from the reply part holding the sentinel to the reply that
                // carries the run's answer; steers the run read sit between them as user messages.
                var first = Messages.IndexOf(message);
                if (first < 0) return;
                var last = first;
                while (last + 1 < Messages.Count
                    && string.Equals(Messages[last + 1].SourceRunId, paging.RunId, StringComparison.Ordinal))
                {
                    last++;
                }

                var answer = Messages[last];
                var merged = RunActivityPageMerger.Merge(shown, page);
                answer.Content = paging.PersistedText ?? string.Empty;
                var parts = SessionTranscriptBuilder.ApplyReplayAndSplit(answer, merged, paging.PersistedText);

                var scroller = ChatScrollViewer;
                var offset = scroller?.VerticalOffset ?? 0;
                var extent = scroller?.ExtentHeight ?? 0;
                _holdOlderActivitySentinels = true;
                for (var index = last; index >= first; index--) Messages.RemoveAt(index);
                for (var index = 0; index < parts.Count; index++) Messages.Insert(first + index, parts[index]);

                _historyReplay = _historyReplay
                    .Select(run => string.Equals(run.RunId, paging.RunId, StringComparison.Ordinal) ? merged : run)
                    .ToArray();
                // The checklist panel already shows the latest list; older rows only register.
                _agentActivityFeed.RegisterReplayedMessages(parts);
                HydrateReplayedImages();
                _sidePanelEditsDirty = true;
                RefreshSidePanelEdits();

                // Everything new sits above what the reader sees: move down by what was added.
                void KeepReaderInPlace()
                {
                    if (ChatScrollViewer is not { } settled) return;
                    var added = Math.Max(0, settled.ExtentHeight - extent);
                    settled.ChangeView(null, Math.Min(offset + added, settled.ScrollableHeight), null, disableAnimation: true);
                    _followOutput = false;
                }

                RunAfterLayout(KeepReaderInPlace);
                // RunAfterLayout skips its action when the reader scrolls first; the hold is
                // released either way, after it, from the same low-priority queue.
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => _holdOlderActivitySentinels = false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Opening another session cancels the read; only its transcript owns the view.
            }
            catch (Exception exception)
            {
                if (OwnsSessionHistoryLoad(sessionId, generation, CancellationToken.None))
                {
                    GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Earlier activity load failed: {exception.Message}");
                    ShowAgentInlineStatus("Could not load earlier activity. Scroll up to try again.");
                }
            }
            finally
            {
                paging.IsLoading = false;
                if (!OwnsSessionHistoryLoad(sessionId, generation, CancellationToken.None)) _holdOlderActivitySentinels = false;
            }
        }
    }
}
