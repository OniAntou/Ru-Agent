using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RuApp.Services;
using VirtualKey = Windows.System.VirtualKey;

namespace RuApp.Views
{
    public sealed partial class ChatView
    {
        private bool _newMessagesPillWired;
        private bool _followOutput = true;
        private bool _scrollingDisposed;
        private long _scrollInteractionGeneration;
        private long _transcriptRevision;

        private readonly record struct TranscriptAnchor(int Index, double ViewportY);

        private TranscriptAnchor? CaptureTranscriptAnchor()
        {
            if (ChatListView.ItemsPanelRoot is not ItemsStackPanel panel || ChatScrollViewer is not { } scroller)
                return null;
            var index = panel.FirstVisibleIndex;
            if (index < 0 || ChatListView.ContainerFromIndex(index) is not FrameworkElement row)
                return null;
            return new TranscriptAnchor(index, row.TransformToVisual(scroller).TransformPoint(new Windows.Foundation.Point()).Y);
        }

        private void RestoreTranscriptAnchor(TranscriptAnchor? anchor, double fallbackOffset)
        {
            if (anchor is { } saved && saved.Index < Messages.Count)
            {
                ChatListView.ScrollIntoView(Messages[saved.Index], ScrollIntoViewAlignment.Leading);
                ChatListView.UpdateLayout();
                if (ChatScrollViewer is { } scroller && ChatListView.ContainerFromIndex(saved.Index) is FrameworkElement row)
                {
                    var y = row.TransformToVisual(scroller).TransformPoint(new Windows.Foundation.Point()).Y;
                    var target = ChatScrollPolicy.RestoreAnchorOffset(scroller.VerticalOffset, y, saved.ViewportY, scroller.ScrollableHeight);
                    scroller.ChangeView(null, target, null, disableAnimation: true);
                    return;
                }
            }
            if (ChatScrollViewer is { } fallback)
                fallback.ChangeView(null, Math.Min(fallbackOffset, fallback.ScrollableHeight), null, disableAnimation: true);
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


        private void ScrollChatToBottom()
        {
            var scroller = ChatScrollViewer;
            if (scroller is null)
            {
                QueueScrollRetry();
                return;
            }

            var maximum = scroller.ScrollableHeight;
            var offset = scroller.VerticalOffset;
            if (!ChatScrollPolicy.IsConsistent(offset, maximum)) return;
            if (maximum > 0)
            {
                if (ChatScrollPolicy.CanFollow(offset, maximum, _followOutput))
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
                        // The follow scroll glides with the growing reply instead of
                        // snapping each batch; a reader with animations off keeps the
                        // instant jump, and every deliberate jump elsewhere still
                        // disables animation explicitly.
                        scroller.ChangeView(null, maximum, null, disableAnimation: !UiTiming.AnimationsEnabled);
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
            var generation = _sessionOpenGeneration;
            var interaction = _scrollInteractionGeneration;
            var revision = _transcriptRevision;
            if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                _scrollRetryQueued = false;
                if (_scrollingDisposed || generation != _sessionOpenGeneration
                    || interaction != _scrollInteractionGeneration || revision != _transcriptRevision) return;
                RetryScrollAfterLayout();
            })) _scrollRetryQueued = false;
        }

        private void RetryScrollAfterLayout()
        {
            _scrollRetryQueued = false;
            var scroller = ChatScrollViewer;
            if (scroller is null)
            {
                return;
            }

            var maximum = scroller.ScrollableHeight;
            var offset = scroller.VerticalOffset;
            if (!ChatScrollPolicy.IsConsistent(offset, maximum)) return;
            if (maximum > 0)
            {
                if (ChatScrollPolicy.CanFollow(offset, maximum, _followOutput))
                {
                    _lastScrollChangeTick = Environment.TickCount64;
                    scroller.ChangeView(null, maximum, null, disableAnimation: !UiTiming.AnimationsEnabled);
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

        /// <summary>
        /// Runs an action after the pending layout pass. A bulk ReplaceAll drops
        /// the virtualized panel before it measures, so offsets read synchronously
        /// right after it still belong to the old content: scrolling on those
        /// numbers lands mid-transcript, or parks a fresh open at its oldest
        /// message while the reader watches the top.
        /// </summary>
        private void RunAfterLayout(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            var generation = _sessionOpenGeneration;
            var interaction = _scrollInteractionGeneration;
            var revision = _transcriptRevision;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    if (_scrollingDisposed || generation != _sessionOpenGeneration
                        || interaction != _scrollInteractionGeneration || revision != _transcriptRevision) return;
                    // Dispatcher priority alone does not flush XAML layout. Do this
                    // only for bulk restores, never on each streaming delta.
                    ChatListView.UpdateLayout();
                    EnsureNewMessagesPillWired();
                    if (interaction != _scrollInteractionGeneration || generation != _sessionOpenGeneration
                        || revision != _transcriptRevision || _scrollingDisposed) return;
                    action();
                }
                catch (Exception ex)
                {
                    GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Deferred layout action failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Lands a freshly opened session on its latest message. Runs after
        /// layout: the ReplaceAll above reset the panel, so only post-layout
        /// numbers point at the real bottom.
        /// </summary>
        private void ScrollToLatestAfterLayout()
        {
            if (Messages.Count == 0) return;
            ChatListView.ScrollIntoView(Messages[Messages.Count - 1], ScrollIntoViewAlignment.Default);
            ChatListView.UpdateLayout();
            var scroller = ChatScrollViewer;
            if (scroller is null || !ChatScrollPolicy.IsConsistent(scroller.VerticalOffset, scroller.ScrollableHeight)
                || scroller.ScrollableHeight <= 0)
            {
                return;
            }

            _lastScrollChangeTick = Environment.TickCount64;
            scroller.ChangeView(null, scroller.ScrollableHeight, null, disableAnimation: true);
            _followOutput = true;
        }


        private void InitializeChatScrolling()
        {
            ChatListView.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnTranscriptScrollInput), true);
            ChatListView.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnTranscriptScrollInput), true);
            ChatListView.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnTranscriptScrollKey), true);
        }

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
            scroller.ViewChanged += OnChatScrollViewChanged;
        }

        private void OnChatScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            var scroller = ChatScrollViewer;
            if (scroller is null || NewMessagesPill is null)
            {
                return;
            }

            double maxOffset = scroller.ScrollableHeight;
            _followOutput = ChatScrollPolicy.FollowAfterViewChange(scroller.VerticalOffset, maxOffset, _followOutput);
            if (_followOutput)
            {
                NewMessagesPill.Visibility = Visibility.Collapsed;
                // Realizing variable-height history changes the extent after
                // ScrollIntoView completes. Settle follow against that new extent.
                if (!e.IsIntermediate && !ChatScrollPolicy.IsNearBottom(scroller.VerticalOffset, maxOffset))
                    QueueScrollRetry();
            }
        }

        private void OnTranscriptScrollInput(object sender, PointerRoutedEventArgs e)
        {
            _scrollInteractionGeneration++;
            _followOutput = false;
        }

        private void OnTranscriptScrollKey(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End)
            {
                _scrollInteractionGeneration++;
                _followOutput = false;
            }
        }

        private void DisposeChatScrolling()
        {
            _scrollingDisposed = true;
            if (_chatScrollViewer is not null) _chatScrollViewer.ViewChanged -= OnChatScrollViewChanged;
            ChatListView.RemoveHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnTranscriptScrollInput));
            ChatListView.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnTranscriptScrollInput));
            ChatListView.RemoveHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnTranscriptScrollKey));
            _newMessagesPillWired = false;
        }

        private void ShowNewMessagesPill()
        {
            if (_followOutput || NewMessagesPill is null)
            {
                return;
            }

            if (NewMessagesPill.Visibility == Visibility.Visible)
            {
                return;
            }

            NewMessagesPill.Visibility = Visibility.Visible;
            PlayNewMessagesPillEntrance();
        }

        /// <summary>The pill rises into place once; hiding stays instant.</summary>
        private void PlayNewMessagesPillEntrance()
        {
            if (NewMessagesPillSlide is null)
            {
                return;
            }

            if (!Services.UiTiming.AnimationsEnabled)
            {
                NewMessagesPill.Opacity = 1;
                NewMessagesPillSlide.Y = 0;
                return;
            }

            NewMessagesPill.Opacity = 0;
            NewMessagesPillSlide.Y = 8;

            var fade = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fade, NewMessagesPill);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var rise = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(rise, NewMessagesPillSlide);
            Storyboard.SetTargetProperty(rise, "Y");

            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Children.Add(rise);
            storyboard.Begin();
        }

        private void OnNewMessagesPillClick(object sender, RoutedEventArgs e)
        {
            _followOutput = true;
            if (NewMessagesPill is not null)
            {
                NewMessagesPill.Visibility = Visibility.Collapsed;
            }

            JumpToLatestForNewTurn();
            RunAfterLayout(ScrollToLatestAfterLayout);
            ChatInputTextBox?.Focus(FocusState.Programmatic);
        }

        private void JumpToLatestForNewTurn()
        {
            _scrollInteractionGeneration++;
            _followOutput = true;
            if (NewMessagesPill is not null)
            {
                NewMessagesPill.Visibility = Visibility.Collapsed;
            }
        }


    }
}
