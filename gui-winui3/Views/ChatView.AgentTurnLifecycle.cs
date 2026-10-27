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
    }
}
