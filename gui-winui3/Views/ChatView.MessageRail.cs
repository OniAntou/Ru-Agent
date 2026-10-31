using System.Collections.Generic;
using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views
{
    /// <summary>
    /// The message rail in the transcript gutter: a tick per loaded message,
    /// a hover preview, and a click that jumps to the message.
    /// </summary>
    public sealed partial class ChatView
    {
        // Leaves the jumped-to message's first line clear of the viewport edge.
        private const double MessageRailJumpInset = 12;

        private bool _messageRailRefreshQueued;
        private ScrollViewer? _messageRailScroller;

        private void InitializeMessageRail()
        {
            Messages.CollectionChanged += OnMessageRailMessagesChanged;
            MessageRail.MessageInvoked += OnMessageRailInvoked;
            ChatContentGrid.SizeChanged += OnMessageRailHostSizeChanged;
            Loaded += OnMessageRailLoaded;
        }

        private void OnMessageRailLoaded(object sender, RoutedEventArgs e) => QueueMessageRailRefresh();

        private void OnMessageRailHostSizeChanged(object sender, SizeChangedEventArgs e) => UpdateMessageRailVisibility();

        // A ReplaceAll raises one Reset and history paging prepends a batch;
        // either way the ticks are rebuilt once, after the dispatcher settles.
        private void OnMessageRailMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueMessageRailRefresh();

        private void QueueMessageRailRefresh()
        {
            if (_messageRailRefreshQueued) return;
            _messageRailRefreshQueued = true;
            if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RefreshMessageRail))
                _messageRailRefreshQueued = false;
        }

        private void RefreshMessageRail()
        {
            _messageRailRefreshQueued = false;
            if (_scrollingDisposed) return;
            EnsureMessageRailScrollWired();

            var entries = new List<MessageRailEntry>(Messages.Count);
            foreach (var message in Messages)
            {
                var item = message;
                entries.Add(new MessageRailEntry(
                    () => MessageRailPolicy.Preview(item.Content, item.Role == "user"),
                    $"{item.RoleLabel} · {item.TimeLabel}"));
            }

            MessageRail.SetEntries(entries);
            UpdateMessageRailVisibility();
            UpdateMessageRailCurrent();
        }

        private void UpdateMessageRailVisibility()
            => MessageRail.Visibility = MessageRailPolicy.HasRoom(ChatContentGrid.ActualWidth, MessageColumnMaxWidth, MessageRail.MessageCount)
                ? Visibility.Visible
                : Visibility.Collapsed;

        private void EnsureMessageRailScrollWired()
        {
            if (_messageRailScroller is not null || ChatScrollViewer is not { } scroller) return;
            _messageRailScroller = scroller;
            scroller.ViewChanged += OnMessageRailViewChanged;
        }

        private void OnMessageRailViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateMessageRailCurrent();

        private void UpdateMessageRailCurrent()
        {
            if (Messages.Count == 0 || ChatListView.ItemsPanelRoot is not ItemsStackPanel panel) return;
            var current = _messageRailScroller is { } scroller
                          && ChatScrollPolicy.IsNearBottom(scroller.VerticalOffset, scroller.ScrollableHeight)
                ? Messages.Count - 1
                : panel.FirstVisibleIndex;
            if (current >= 0) MessageRail.SetCurrentMessage(current);
        }

        /// <summary>
        /// Jumps to a message's first line. The click lands outside the list,
        /// so it stops following explicitly; otherwise the follow anchor would
        /// pull the view straight back to the newest reply. Reaching the
        /// bottom this way resumes following through the normal view change.
        /// </summary>
        private void OnMessageRailInvoked(object? sender, int index)
        {
            if (index < 0 || index >= Messages.Count) return;
            StopFollowingTranscript();
            var anchor = new TranscriptAnchor(index, MessageRailJumpInset);
            void Settle() => RestoreTranscriptAnchor(anchor, ChatScrollViewer?.VerticalOffset ?? 0);
            Settle();
            // Rows realized by the jump replace estimated heights; settle once more.
            RunAfterLayout(Settle);
        }

        private void DisposeMessageRail()
        {
            Messages.CollectionChanged -= OnMessageRailMessagesChanged;
            MessageRail.MessageInvoked -= OnMessageRailInvoked;
            ChatContentGrid.SizeChanged -= OnMessageRailHostSizeChanged;
            Loaded -= OnMessageRailLoaded;
            if (_messageRailScroller is not null) _messageRailScroller.ViewChanged -= OnMessageRailViewChanged;
            _messageRailScroller = null;
        }
    }
}
