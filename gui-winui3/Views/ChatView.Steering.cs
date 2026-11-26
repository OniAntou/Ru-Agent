using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuApp.Services;
using RuApp.ViewModels;

namespace RuApp.Views
{
    /// <summary>
    /// Enter while a run is working (ADR 0056). By default the message, with its files and
    /// images, steers the run: the Host holds it and the model reads it at its next step.
    /// Commands still need a turn. Settings can make Enter queue it as the next turn instead.
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
        private const string SteerNeedsTurnStatus = "Commands cannot steer a run; queued as the next turn.";
        private const string SteerTooLongWithFilesStatus = "The message and its files are too long to steer the run; queued as the next turn.";
        private const string SteerImagesNotReadyStatus = "An image is still being prepared, so the message was queued as the next turn.";

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
            var sent = Composer.TakeSnapshot(ChatInputTextBox?.Text ?? string.Empty);
            var text = sent.Text;
            if (controller is null || sent.IsEmpty)
            {
                QueueCurrentComposerTurn();
                return;
            }

            // Text and code files travel inside the prompt text and images beside it, as they
            // do for a turn, so a steer carries whatever a turn can. Commands need a turn.
            if (text.StartsWith('/'))
            {
                if (QueueCurrentComposerTurn()) ShowAgentInlineStatus(SteerNeedsTurnStatus);
                return;
            }

            // No attachments answers true with a null payload list: a plain message must steer.
            if (!ChatComposerViewModel.TryBuildImagePayloads(sent.Attachments, out var imagePayloads)
                || imagePayloads is { Count: > AgentSteerLimits.MaxImages })
            {
                if (QueueCurrentComposerTurn()) ShowAgentInlineStatus(SteerImagesNotReadyStatus);
                return;
            }

            var steerText = ChatComposerViewModel.BuildEffectivePrompt(text, sent.Attachments);
            if (string.IsNullOrWhiteSpace(steerText) && imagePayloads is not { Count: > 0 })
            {
                // Nothing the run could read (a file with no text yet): it needs a turn.
                QueueCurrentComposerTurn();
                return;
            }

            if (steerText.Length > AgentSteerLimits.MaxCharacters)
            {
                if (sent.Attachments.Count > 0)
                {
                    if (QueueCurrentComposerTurn()) ShowAgentInlineStatus(SteerTooLongWithFilesStatus);
                    return;
                }

                ShowAgentInlineStatus($"A message to a working run is at most {AgentSteerLimits.MaxCharacters:N0} characters. Shorten it, or press Stop and send it as a new turn.");
                return;
            }

            // Cleared before the request so a second Enter cannot send it twice; every
            // outcome other than a sent steer puts the text and its files back.
            var sessionId = _activeSessionId;
            Composer.ClearAfterSend(sessionId);
            AgentSteerOutcome outcome;
            try
            {
                outcome = await controller.SteerAsync(steerText, imagePayloads, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] steer failed: {ex.GetType().Name}");
                outcome = new AgentSteerOutcome(AgentSteerAttempt.Refused, "steer_failed");
            }

            switch (outcome.Attempt)
            {
                case AgentSteerAttempt.Sent when outcome.State == AgentSteerLimits.StateDelivered:
                    // A retried send answered with the steer already read: show the message here
                    // instead of a waiting copy for a delivery that already happened.
                    ContinueTurnAfterSteer(SteerMessage(steerText, sent.Attachments));
                    break;
                case AgentSteerAttempt.Sent when AgentSteerLimits.IsValidSteerId(outcome.SteerId):
                    ShowWaitingSteer(SteerActivityId(outcome.SteerId!), steerText, sent.Attachments);
                    break;
                case AgentSteerAttempt.Sent:
                    ContinueTurnAfterSteer(SteerMessage(steerText, sent.Attachments));
                    break;
                case AgentSteerAttempt.NotSteerable:
                    // The run had just ended: the message and its files are simply the next turn.
                    RestoreUnsentSteer(sessionId, sent);
                    QueueCurrentComposerTurn();
                    break;
                default:
                    RestoreUnsentSteer(sessionId, sent);
                    ShowAgentInlineStatus(SteerRefusedStatus(outcome.ErrorCode));
                    break;
            }
        }

        /// <summary>The id of the activity the Host publishes when the run reads this steer.</summary>
        private static string SteerActivityId(string steerId) =>
            "act_" + steerId[AgentSteerLimits.IdPrefix.Length..] + "_steer";

        /// <summary>
        /// A steer that was not sent goes back to the composer: its files as chips again,
        /// its text in front of anything typed since.
        /// </summary>
        private void RestoreUnsentSteer(string? sessionId, ComposerSnapshot sent)
        {
            Composer.ReturnAttachments(sessionId, ChatInputTextBox?.Text ?? string.Empty, sent.Attachments);
            if (sent.Text.Length > 0) RestoreToComposer(new[] { sent.Text });
        }

        /// <summary>
        /// The user's message a steer becomes: the typed text, with the files it carried shown
        /// as chips rather than as their inlined text. This window's own steer shows its images;
        /// one read from the Host shows how many it carried, as a reopened turn does.
        /// </summary>
        private Models.ChatMessageItem SteerMessage(string text, IReadOnlyList<Models.ImageAttachmentItem>? sentAttachments = null)
        {
            var (written, imageCount) = AgentSteerText.Split(text);
            var message = new Models.ChatMessageItem
            {
                Role = "user",
                ArrivesLive = true,
                Content = written,
                SessionId = _activeSessionId,
                Attachments = new ObservableCollection<Models.ImageAttachmentItem>(),
            };
            AttachedPromptDisplay.Apply(message);
            var images = sentAttachments?.Where(attachment => attachment.IsImage).ToList() ?? new();
            foreach (var image in images) message.Attachments!.Add(image);
            if (images.Count == 0 && imageCount > 0) message.MissingImageCount = imageCount;
            return message;
        }

        /// <summary>
        /// The user's message at the tail of the chat, waiting for the run to read it. It is
        /// drawn like any message; only its place says it has not been read yet.
        /// </summary>
        private void ShowWaitingSteer(string activityId, string text, IReadOnlyList<Models.ImageAttachmentItem> attachments)
        {
            if (!_liveSteerActivityIds.Add(activityId)) return;
            var waiting = SteerMessage(text, attachments);
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
                // The same message, with its file chips, moves to where the run read it.
                Messages.Remove(waiting);
                ContinueTurnAfterSteer(waiting);
                return;
            }

            if (_liveSteerActivityIds.Add(activityId) && !string.IsNullOrWhiteSpace(text))
            {
                // Sent from another window: its images are read back like a reopened chat's.
                var steer = SteerMessage(text);
                steer.SourceRunId = _agentController?.CurrentRun?.RunId;
                steer.SourceSteerId = ReplayImageAssignment.SteerIdFromActivityId(activityId);
                ContinueTurnAfterSteer(steer);
                HydrateReplayedImages();
            }
        }

        /// <summary>
        /// Shows a read steer the way any message is shown: the user's bubble ends Ru's reply
        /// so far, and what Ru does next continues in a new reply below it, as if the two had
        /// simply kept messaging. The run is the same one; only the transcript splits. Steers
        /// still waiting stay below, at the tail.
        /// </summary>
        private void ContinueTurnAfterSteer(Models.ChatMessageItem steer)
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

            AddAboveWaitingSteers(steer);
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
        /// A message the run ended without reading is retained by the session, so it stays at
        /// the tail of the chat until the next run of this chat reads it (ADR 0056). The
        /// transcript reload that ends a turn drops rows that are not in history yet; this
        /// puts the waiting copies back so the user keeps seeing what they wrote.
        /// </summary>
        private void RestoreWaitingSteerCopies()
        {
            foreach (var waiting in _waitingSteers.Values)
            {
                if (!Messages.Contains(waiting)) Messages.Add(waiting);
            }
        }

        /// <summary>
        /// Says a message was not read, so nobody wonders why it went unanswered: the next
        /// message they send in this chat brings it to Ru.
        /// </summary>
        private void ShowRetainedSteerNotice()
        {
            var unread = _waitingSteers.Count;
            if (unread == 0) return;
            ShowAgentInlineStatus(unread == 1
                ? "Ru did not read your message · it will be sent with your next message."
                : $"{unread} messages Ru did not read will be sent with your next message.");
        }
    }
}
