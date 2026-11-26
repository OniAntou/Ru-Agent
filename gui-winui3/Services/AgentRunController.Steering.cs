using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuApp.Services;

public enum AgentSteerAttempt
{
    /// <summary>The Host holds the steer; the model reads it at its next step.</summary>
    Sent,

    /// <summary>No working run takes it: send the text as the next turn instead.</summary>
    NotSteerable,

    /// <summary>The Host refused it; the text stays with the user.</summary>
    Refused,
}

/// <summary>What a steer attempt did, with the Host's reported state when it was accepted.</summary>
public readonly record struct AgentSteerOutcome(AgentSteerAttempt Attempt, string? ErrorCode = null, string? SteerId = null, string? State = null);

/// <summary>
/// Steering (ADR 0056): a message the user sends into the run in hand. A steer the run never
/// reads is retained by the session, so this window keeps no copy and sends nothing again.
/// </summary>
public sealed partial class AgentRunController
{
    public Task<AgentSteerOutcome> SteerAsync(string text, CancellationToken cancellationToken = default) =>
        SteerAsync(text, null, cancellationToken);

    /// <summary>Sends text and image data URLs into the run in hand.</summary>
    public async Task<AgentSteerOutcome> SteerAsync(string text, IReadOnlyList<string>? images, CancellationToken cancellationToken = default)
    {
        var runId = CurrentRun?.RunId;
        if (string.IsNullOrWhiteSpace(runId)) return new AgentSteerOutcome(AgentSteerAttempt.NotSteerable);
        var steerId = AgentSteerLimits.NewSteerId();
        try
        {
            var sent = images is { Count: > 0 }
                ? await _client.SteerAsync(runId, steerId, text, images, cancellationToken).ConfigureAwait(false)
                : await _client.SteerAsync(runId, steerId, text, cancellationToken).ConfigureAwait(false);
            // A retried send answers from the stored row. A steer it reports as returned is
            // retained by the session: the waiting copy moves when this chat's next run reads
            // it, so it is never queued again as a turn of its own.
            return new AgentSteerOutcome(AgentSteerAttempt.Sent, SteerId: steerId, State: sent.State);
        }
        catch (AgentApiException error) when (error.ErrorCode is "run_not_steerable" or "run_not_found")
        {
            return new AgentSteerOutcome(AgentSteerAttempt.NotSteerable, error.ErrorCode);
        }
        catch (AgentApiException error)
        {
            return new AgentSteerOutcome(AgentSteerAttempt.Refused, error.ErrorCode);
        }
    }
}
