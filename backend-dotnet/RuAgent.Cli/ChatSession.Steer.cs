using System.Collections.Immutable;
using System.Linq;
using RuAgent.Cli.Chat;
using RuAgent.Cli.Images;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        /// <summary>
        /// Sends Enter's text and images into the working run (ADR 0056). The transcript shows
        /// it when the model receives it, as the run's own <c>steer</c> activity. A steer the run
        /// never reads is retained by the session and read by its next run, so this session keeps
        /// no copy and sends nothing again.
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
                // A retried send answers from the stored row. A steer it reports as returned is
                // retained by the session and this chat's next run reads it, so it is never
                // queued again as a turn of its own.
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
    }
}
