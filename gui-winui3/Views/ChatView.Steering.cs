using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Protocol.Harness;
using RuApp.Services;

namespace RuApp.Views
{
    /// <summary>
    /// Enter while a run is working (ADR 0056). By default the message steers the run: the
    /// Host holds it and the model reads it at its next step. Settings can make Enter queue
    /// it as the next turn instead.
    /// </summary>
    /// <remarks>
    /// Presentation follows DeepSeek Harness. A sent steer appears at once as the user's
    /// message at the tail of the chat, drawn exactly like the message it becomes; Ru's work
    /// keeps arriving above it. When the run reads the steer, the waiting message is replaced
    /// by the message at that point: Ru's reply so far ends, the user's message follows, and
    /// Ru continues in a new reply below it. A reload draws the same order. A steer the run
    /// ends without reading (it finished, or the user pressed Stop) is sent as the next turn,
    /// so Stop also works as "send it now".
    /// </remarks>
    public sealed partial class ChatView
    {
        private const string SteerNeedsTurnStatus = "Attachments and commands cannot steer a run; queued as the next turn.";

        /// <summary>Messages sent into the run and not yet read by it, by delivery activity id.</summary>
        private readonly Dictionary<string, Models.ChatMessageItem> _waitingSteers = new(StringComparer.Ordinal);

        private async Task SteerOrQueueCurrentComposerTurnAsync()
        {
            if (ComposerSendPreferenceStore.Shared.Current.WhileRunning == ComposerWhileRunning.Queue)
            {
                QueueCurrentComposerTurn();
                return;
            }

            var controller = _agentController;
            var text = (ChatInputTextBox?.Text ?? string.Empty).Trim();
            if (controller is null || text.Length == 0)
            {
                QueueCurrentComposerTurn();
                return;
            }

            // A steer is plain text into the pinned run: files, images and commands need a turn.
            if (Composer.Attachments.Count > 0 || text.StartsWith('/'))
            {
                if (QueueCurrentComposerTurn()) ShowAgentInlineStatus(SteerNeedsTurnStatus);
                return;
            }

            if (text.Length > AgentSteerLimits.MaxCharacters)
            {
                ShowAgentInlineStatus($"A message to a working run is at most {AgentSteerLimits.MaxCharacters:N0} characters. Shorten it, or press Stop and send it as a new turn.");
                return;
            }

            // Cleared before the request so a second Enter cannot send it twice; every
            // outcome other than a sent steer puts the text back.
            Composer.ClearText();
            AgentSteerOutcome outcome;
            try
            {
                outcome = await controller.SteerAsync(text, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] steer failed: {ex.GetType().Name}");
                outcome = new AgentSteerOutcome(AgentSteerAttempt.Refused, "steer_failed");
            }

            switch (outcome.Attempt)
            {
                case AgentSteerAttempt.Sent when AgentSteerLimits.IsValidSteerId(outcome.SteerId):
                    ShowWaitingSteer(SteerActivityId(outcome.SteerId!), text);
                    break;
                case AgentSteerAttempt.Sent:
                    ContinueTurnAfterSteer(text);
                    break;
                case AgentSteerAttempt.NotSteerable:
                    // The run had just ended: the message is simply the next turn.
                    RestoreToComposer(new[] { text });
                    QueueCurrentComposerTurn();
                    break;
                default:
                    RestoreToComposer(new[] { text });
                    ShowAgentInlineStatus(SteerRefusedStatus(outcome.ErrorCode));
                    break;
            }
        }

        /// <summary>The id of the activity the Host publishes when the run reads this steer.</summary>
        private static string SteerActivityId(string steerId) =>
            "act_" + steerId[AgentSteerLimits.IdPrefix.Length..] + "_steer";

        /// <summary>
        /// The user's message at the tail of the chat, waiting for the run to read it. It is
        /// drawn like any message; only its place says it has not been read yet.
        /// </summary>
        private void ShowWaitingSteer(string activityId, string text)
        {
            if (!_liveSteerActivityIds.Add(activityId)) return;
            var waiting = new Models.ChatMessageItem
            {
                Role = "user",
                ArrivesLive = true,
                Content = text,
                SessionId = _activeSessionId,
                Attachments = new ObservableCollection<Models.ImageAttachmentItem>(),
            };
            _waitingSteers[activityId] = waiting;
            Messages.Add(waiting);
            ScrollChatToBottom();
        }

        /// <summary>
        /// The run read a steer: its waiting message moves to this point of the run. A steer
        /// sent from another window appears here directly.
        /// </summary>
        private void OnSteerDelivered(string activityId, string? text)
        {
            if (_waitingSteers.Remove(activityId, out var waiting))
            {
                Messages.Remove(waiting);
                ContinueTurnAfterSteer(waiting.Content ?? text ?? string.Empty);
                return;
            }

            if (_liveSteerActivityIds.Add(activityId) && !string.IsNullOrWhiteSpace(text))
                ContinueTurnAfterSteer(text);
        }

        /// <summary>
        /// Shows a read steer the way any message is shown: the user's bubble ends Ru's reply
        /// so far, and what Ru does next continues in a new reply below it, as if the two had
        /// simply kept messaging. The run is the same one; only the transcript splits. Steers
        /// still waiting stay below, at the tail.
        /// </summary>
        private void ContinueTurnAfterSteer(string text)
        {
            var before = _activeAgentAssistantMessage;
            if (before is not null && Messages.Contains(before))
            {
                _replyRenderScheduler.ApplyPending();
                FlushPendingReplyText();
                before.AgentActivities = AgentActivities.Count > 0
                    ? new ObservableCollection<Models.AgentActivityItem>(AgentActivities)
                    : null;
                before.ActivitySplitOffsets = before.AgentActivities is { } kept ? TakeSplitOffsetSnapshot(kept) : null;
                before.ContinuesInLaterReply = true;
                before.IsStreaming = false;
                before.SettleSegments();
                UpdateLiveTailText(before);
                _replyTextBase += before.Content?.Length ?? 0;
                // Rows of the earlier reply keep updating through the feed's item map; the
                // live collection now belongs to the reply that follows the message.
                AgentActivities.Clear();
                _activitySplitOffsetById.Clear();
                _segmentFrozenLength = 0;
                if (before.Segments is null && before.AgentActivities is null && string.IsNullOrWhiteSpace(before.Content))
                {
                    Messages.Remove(before);
                }
            }

            AddAboveWaitingSteers(new Models.ChatMessageItem
            {
                Role = "user",
                ArrivesLive = true,
                Content = text,
                SessionId = _activeSessionId,
                Attachments = new ObservableCollection<Models.ImageAttachmentItem>(),
            });
            // The run's one change card lands on its last reply and covers the replies above.
            var earlier = new List<Models.AgentActivityItem>();
            if (before is not null)
            {
                if (before.EarlierReplyActivities is { } older) earlier.AddRange(older);
                if (before.AgentActivities is { } shownBefore) earlier.AddRange(shownBefore);
            }

            _activeAgentAssistantMessage = new Models.ChatMessageItem
            {
                Role = "assistant",
                Content = string.Empty,
                IsStreaming = true,
                SessionId = _activeSessionId,
                ArrivesLive = true,
                EarlierReplyActivities = earlier.Count > 0 ? earlier : null,
            };
            _activeAgentAssistantMessage.AgentActivities = AgentActivities;
            AddAboveWaitingSteers(_activeAgentAssistantMessage);
            ScrollChatToBottom();
        }

        /// <summary>Messages of the run stay above steers that are still waiting at the tail.</summary>
        private void AddAboveWaitingSteers(Models.ChatMessageItem item)
        {
            var firstWaiting = _waitingSteers.Count == 0
                ? -1
                : _waitingSteers.Values.Select(waiting => Messages.IndexOf(waiting)).Where(index => index >= 0)
                    .DefaultIfEmpty(-1).Min();
            if (firstWaiting >= 0) Messages.Insert(firstWaiting, item);
            else Messages.Add(item);
        }

        private static string SteerRefusedStatus(string? errorCode) => errorCode switch
        {
            "prompt_blocked_by_hook" => "A prompt hook refused the message. It is back in the box.",
            "steer_queue_full" => $"{AgentSteerLimits.MaxPending} messages are already waiting for this run. Yours is back in the box.",
            _ => "The message could not be sent. It is back in the box.",
        };

        /// <summary>
        /// Puts text back in the composer. Text typed since stays after it, so nothing the
        /// user wrote is overwritten.
        /// </summary>
        private void RestoreToComposer(IReadOnlyList<string> texts)
        {
            if (texts.Count == 0 || ChatInputTextBox is null) return;
            var current = ChatInputTextBox.Text ?? string.Empty;
            var restored = string.Join(Environment.NewLine, texts);
            ChatInputTextBox.Text = string.IsNullOrEmpty(current) ? restored : restored + Environment.NewLine + current;
            ChatInputTextBox.SelectionStart = ChatInputTextBox.Text.Length;
        }

        /// <summary>
        /// After a turn ends, messages its run never read are sent as the next turn, before
        /// any queued turn: they were meant for now. This is also what Stop does with them.
        /// Returns whether a turn was started, so the caller leaves the queue for later.
        /// </summary>
        private async Task<bool> SendReturnedSteersAsync(string? runId, CancellationToken cancellationToken)
        {
            if (_agentController is not { } controller || string.IsNullOrWhiteSpace(runId)) return false;
            IReadOnlyList<AgentReturnedSteer> taken;
            try
            {
                taken = await controller.TakeReturnedSteersAsync(runId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A cancelled or failed read keeps them tracked; it never blocks the queue drain.
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] returned steers could not be read: {ex.GetType().Name}");
                return false;
            }

            if (taken.Count == 0) return false;
            // Only the copies of messages actually taken leave the tail; the turn they start
            // shows the message itself.
            foreach (var steer in taken)
            {
                if (_waitingSteers.Remove(SteerActivityId(steer.SteerId), out var waiting)) Messages.Remove(waiting);
            }

            var returned = taken.Select(steer => steer.Text).ToArray();
            var text = string.Join(Environment.NewLine + Environment.NewLine, returned);
            if (!ModelSelection.TryCaptureRunSelection(out var providerSelection))
            {
                RestoreToComposer(returned);
                ShowAgentInlineStatus("The run ended before reading your message, and the model selection is still loading. It is back in the box.");
                return false;
            }

            return await SendAgentTurnAsync(text, cancellationToken, providerSelection, displayUserText: text);
        }

        /// <summary>
        /// Stop with messages waiting sends them at once. The Host settles a cancelled run
        /// shortly after it accepts the cancel, so the waiting messages read as returned only
        /// then: ask again briefly instead of losing them.
        /// </summary>
        private async Task SendWaitingSteersAfterStopAsync(string? runId)
        {
            if (string.IsNullOrWhiteSpace(runId) || _agentController is not { } controller) return;
            for (var attempt = 0; attempt < 15 && controller.HasTrackedSteers(runId); attempt++)
            {
                if (await SendReturnedSteersAsync(runId, CancellationToken.None)) return;
                await Task.Delay(200);
            }
        }
    }
}
