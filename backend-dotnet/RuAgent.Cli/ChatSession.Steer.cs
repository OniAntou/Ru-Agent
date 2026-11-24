using System.Collections.Immutable;
using RuAgent.Cli.Chat;
using RuAgent.Cli.Images;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        private readonly object steerGate = new();
        // Images stay here until the steer settles: a steer the run never reads is sent again
        // as the next turn, and the Host's steer list carries no image data.
        private readonly List<(string RunId, string SteerId, string? SessionId, ImmutableArray<CliImageAttachment> Images)> sentSteers = [];

        /// <summary>
        /// Sends Enter's text and images into the working run (ADR 0056). The transcript shows
        /// it when the model receives it, as the run's own <c>steer</c> activity.
        /// </summary>
        private async Task<SteerResult> SteerActiveRunAsync(string text, ImmutableArray<CliImageAttachment> images)
        {
            var runId = runSession.ActiveRunId;
            if (runId is null) return new SteerResult(SteerAttempt.NotSteerable);
            var steerId = AgentSteerLimits.NewSteerId();
            try
            {
                await _agentClient.SteerAsync(runId, steerId, text,
                    images.IsEmpty ? null : images.Select(image => image.ToDataUrl()).ToArray(), cancellationToken).ConfigureAwait(false);
                lock (steerGate) sentSteers.Add((runId, steerId, sessionId, images));
                // The Host holds the images now; only their in-memory copy is kept for a return.
                ChatSubmissionState.Release(images);
                return new SteerResult(SteerAttempt.Sent);
            }
            catch (AgentApiException error) when (error.ErrorCode is "run_not_steerable" or "run_not_found")
            {
                return new SteerResult(SteerAttempt.NotSteerable);
            }
            catch (AgentApiException error)
            {
                return new SteerResult(SteerAttempt.Refused, error.ErrorCode switch
                {
                    "prompt_blocked_by_hook" => "a prompt hook refused the steer · your text is back in the prompt",
                    "steer_queue_full" => "too many messages are already waiting · your text is back in the prompt",
                    _ => $"steer refused ({error.ErrorCode}) · your text is back in the prompt",
                });
            }
        }

        /// <summary>
        /// After a turn, messages its run never read (it ended first, or Esc stopped it) are
        /// sent as the next turn, ahead of queued prompts. Delivered ones are forgotten; ones
        /// still pending stay tracked for a parked run.
        /// </summary>
        private async Task SendReturnedSteersAsync(CancellationToken ct)
        {
            // Esc only asks the Host to cancel; the run settles a moment later, and only then do
            // its waiting messages read as returned. Ask again briefly while a run that is no
            // longer this chat's active run still holds one, instead of sending it a turn late.
            for (var attempt = 0; attempt < 15; attempt++)
            {
                await SendSettledSteersAsync(ct).ConfigureAwait(false);
                bool waitingOnEndedRun;
                lock (steerGate)
                {
                    waitingOnEndedRun = sentSteers.Any(item => !string.Equals(item.RunId, runSession.ActiveRunId, StringComparison.Ordinal)
                        && string.Equals(item.SessionId, sessionId, StringComparison.Ordinal));
                }

                if (!waitingOnEndedRun || ct.IsCancellationRequested) return;
                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task SendSettledSteersAsync(CancellationToken ct)
        {
            (string RunId, string SteerId, string? SessionId, ImmutableArray<CliImageAttachment> Images)[] tracked;
            lock (steerGate) tracked = [.. sentSteers];
            if (tracked.Length == 0) return;

            var returned = new List<string>();
            var returnedImages = new List<CliImageAttachment>();
            var settled = new HashSet<string>(StringComparer.Ordinal);
            foreach (var runId in tracked.Select(item => item.RunId).Distinct(StringComparer.Ordinal))
            {
                AgentSteerListResponse listed;
                try
                {
                    listed = await _agentClient.ListSteersAsync(runId, ct).ConfigureAwait(false);
                }
                catch (Exception error) when (error is AgentApiException or HttpRequestException or OperationCanceledException)
                {
                    // The Host is unreachable or the session is closing: keep tracking them.
                    continue;
                }

                foreach (var steer in listed.Steers)
                {
                    if (steer.State == AgentSteerLimits.StatePending) continue;
                    settled.Add(steer.SteerId);
                    // A message meant for another session's run is never sent into this chat.
                    var mine = tracked.FirstOrDefault(item => item.SteerId == steer.SteerId
                        && string.Equals(item.SessionId, sessionId, StringComparison.Ordinal));
                    if (steer.State == AgentSteerLimits.StateReturned && mine.SteerId is not null)
                    {
                        returned.Add(steer.Text);
                        returnedImages.AddRange(mine.Images);
                    }
                }
            }

            lock (steerGate) sentSteers.RemoveAll(item => settled.Contains(item.SteerId));
            if (returned.Count == 0) return;
            // The images go back to the pending list, so the next turn carries them with the
            // text; a turn holds what one message may, and any beyond that are said, not lost silently.
            var dropped = returnedImages.Count(image => !submissions.TryAddImage(
                image, CliImagePolicy.MaxImageCount, CliImagePolicy.MaxTotalImageBytes, out _));
            if (returnedImages.Count > 0) RefreshAttachmentLabels();
            if (dropped > 0)
                chatEngine?.ShowFeedback($"{dropped} image(s) from waiting messages did not fit the next turn", true);
            chatEngine?.SendReturnedSteers(returned);
        }
    }
}
