using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views
{
    public sealed partial class ChatView
    {
        private SessionHistoryPageCursor _historyPageCursor = new();
        private IReadOnlyList<SessionRunReplay> _historyReplay = Array.Empty<SessionRunReplay>();
        private CancellationToken _historyOpenCancellation;
        private bool _isLoadingEarlierHistory;

        private async Task<IReadOnlyList<SessionHistoryPage>?> FetchLoadedHistoryPagesAsync(
            string sessionId,
            long generation,
            CancellationToken cancellationToken)
        {
            if (_apiService is not { } api) return null;
            bool preserveLoadedPrefix = _historyPageCursor.Owns(sessionId, generation);
            int requestedStart = preserveLoadedPrefix ? _historyPageCursor.StartIndex : int.MaxValue;
            long requestedEpoch = preserveLoadedPrefix ? _historyPageCursor.HistoryEpoch : -1;
            int maximumPages = preserveLoadedPrefix ? _historyPageCursor.LoadedPageCount + 1 : 1;

            var newest = await api.GetSessionHistoryPageAsync(sessionId, cancellationToken: cancellationToken);
            if (newest is null || !OwnsSessionHistoryLoad(sessionId, generation, cancellationToken)) return null;
            var pages = new List<SessionHistoryPage> { newest };
            if (!preserveLoadedPrefix || newest.HistoryEpoch != requestedEpoch) return pages;

            string? cursor = newest.NextCursor;
            int previousStart = newest.StartIndex;
            while (cursor is not null && previousStart > requestedStart && pages.Count < maximumPages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var older = await api.GetSessionHistoryPageAsync(sessionId, cursor, cancellationToken: cancellationToken);
                if (older is null || !OwnsSessionHistoryLoad(sessionId, generation, cancellationToken)) return null;
                if (older.HistoryEpoch != newest.HistoryEpoch || older.StartIndex >= previousStart) break;
                pages.Add(older);
                previousStart = older.StartIndex;
                cursor = older.NextCursor;
            }

            if (previousStart > requestedStart) return null;

            return pages;
        }

        private bool OwnsSessionHistoryLoad(
            string sessionId,
            long generation,
            CancellationToken cancellationToken) =>
            !_scrollingDisposed
            && !cancellationToken.IsCancellationRequested
            && IsCurrentOpen(generation)
            && string.Equals(sessionId, _activeSessionId, StringComparison.Ordinal);

        private bool TryApplyHistoryPages(
            IReadOnlyList<SessionHistoryPage> pages,
            string sessionId,
            long generation)
        {
            if (pages.Count == 0) return false;
            var candidate = new SessionHistoryPageCursor();
            candidate.Reset(sessionId, generation);
            if (!candidate.TrySetNewest(pages[0], sessionId, generation)) return false;
            for (int index = 1; index < pages.Count; index++)
            {
                if (!candidate.TryPrepend(pages[index], sessionId, generation)) return false;
            }

            _historyPageCursor = candidate;
            return true;
        }

        private static List<SessionHistoryEntry> FlattenHistoryPages(IEnumerable<SessionHistoryPage> pages) =>
            pages.SelectMany(page => page.Messages)
                .OrderBy(message => message.MessageIndex)
                .ToList();

        private static bool IsCompleteHistory(IReadOnlyList<SessionHistoryPage> pages) =>
            pages.Count > 0
            && pages[^1].StartIndex == 0
            && pages.Sum(page => page.RawMessageCount) == pages[0].TotalMessages;

        private void UpdateLoadEarlierHistoryButton()
        {
            if (LoadEarlierHistoryButton is null) return;
            LoadEarlierHistoryButton.Visibility = _historyPageCursor.CanLoadEarlier
                ? Visibility.Visible
                : Visibility.Collapsed;
            LoadEarlierHistoryButton.IsEnabled = !_isLoadingEarlierHistory;
            LoadEarlierHistoryButton.Content = _isLoadingEarlierHistory ? "Loading earlier messages…" : "Load earlier messages";
        }

        private async void OnLoadEarlierHistoryClick(object sender, RoutedEventArgs e)
        {
            if (_apiService is not { } api
                || _isLoadingEarlierHistory
                || !_historyPageCursor.CanLoadEarlier
                || !_historyPageCursor.Owns(_activeSessionId, _sessionOpenGeneration))
            {
                return;
            }

            var sessionId = _activeSessionId;
            var generation = _sessionOpenGeneration;
            var cancellationToken = _historyOpenCancellation;
            var cursor = _historyPageCursor.NextCursor;
            if (string.IsNullOrEmpty(cursor)) return;

            _isLoadingEarlierHistory = true;
            UpdateLoadEarlierHistoryButton();
            try
            {
                var page = await api.GetSessionHistoryPageAsync(
                    sessionId,
                    cursor,
                    cancellationToken: cancellationToken);
                if (!OwnsSessionHistoryLoad(sessionId, generation, cancellationToken)) return;
                if (page is null)
                {
                    var refreshed = await ReloadSessionHistoryAsync(cancellationToken, preserveLiveTurn: true);
                    if (refreshed is null && OwnsSessionHistoryLoad(sessionId, generation, cancellationToken))
                    {
                        ShowAgentInlineStatus("Could not load earlier messages. Try again.");
                    }
                    return;
                }

                var earlier = SessionTranscriptBuilder.Build(
                    page.Messages,
                    _historyReplay,
                    sessionId,
                    completeHistory: false);
                if (!_historyPageCursor.TryPrepend(page, sessionId, generation))
                {
                    var refreshed = await ReloadSessionHistoryAsync(cancellationToken, preserveLiveTurn: true);
                    if (refreshed is null && OwnsSessionHistoryLoad(sessionId, generation, cancellationToken))
                    {
                        ShowAgentInlineStatus("Earlier messages changed. Reload the session history and try again.");
                    }
                    return;
                }

                var knownIndices = Messages
                    .Where(message => message.SourceMessageIndex.HasValue)
                    .Select(message => message.SourceMessageIndex!.Value)
                    .ToHashSet();
                var additions = earlier
                    .Where(message => !message.SourceMessageIndex.HasValue
                        || !knownIndices.Contains(message.SourceMessageIndex.Value))
                    .ToArray();
                var preservedOffset = ChatScrollViewer?.VerticalOffset ?? 0;
                var anchor = CaptureTranscriptAnchor();

                for (int index = additions.Length - 1; index >= 0; index--)
                {
                    Messages.Insert(0, additions[index]);
                }

                UpdateTodoPanel(_agentActivityFeed.RegisterReplayedMessages(additions));
                _sidePanelEditsDirty = true;
                RefreshSidePanelEdits();
                UpdateLoadEarlierHistoryButton();
                if (additions.Length > 0)
                {
                    TranscriptAnchor? shiftedAnchor = anchor is { } saved
                        ? new TranscriptAnchor(saved.Index + additions.Length, saved.ViewportY)
                        : null;
                    RunAfterLayout(() =>
                    {
                        RestoreTranscriptAnchor(shiftedAnchor, preservedOffset);
                        _followOutput = false;
                    });
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Opening another session cancels the request; only its transcript owns the view.
            }
            catch (Exception exception)
            {
                if (OwnsSessionHistoryLoad(sessionId, generation, cancellationToken))
                {
                    GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Earlier history load failed: {exception.Message}");
                    ShowAgentInlineStatus("Could not load earlier messages. Try again.");
                }
            }
            finally
            {
                if (OwnsSessionHistoryLoad(sessionId, generation, CancellationToken.None))
                {
                    _isLoadingEarlierHistory = false;
                    UpdateLoadEarlierHistoryButton();
                }
            }
        }
    }
}
