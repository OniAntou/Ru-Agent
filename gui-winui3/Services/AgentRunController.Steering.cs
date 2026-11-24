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

/// <summary>A steer its run ended without reading, to be sent as the next turn.</summary>
public readonly record struct AgentReturnedSteer(string SteerId, string Text);

public readonly record struct AgentSteerOutcome(AgentSteerAttempt Attempt, string? ErrorCode = null, string? SteerId = null);

/// <summary>
/// Steering (ADR 0056): a message the user sends into the run in hand. The controller
/// remembers which steers this window sent so the ones the run never read (it ended
/// first) can go back to the composer.
/// </summary>
public sealed partial class AgentRunController
{
    private readonly object _steerGate = new();

    // Stop and the turn's completion can both ask; one of them takes each returned steer.
    private readonly SemaphoreSlim _returnedGate = new(1, 1);
    private readonly List<(string RunId, string SteerId)> _sentSteers = new();

    public async Task<AgentSteerOutcome> SteerAsync(string text, CancellationToken cancellationToken = default)
    {
        var runId = CurrentRun?.RunId;
        if (string.IsNullOrWhiteSpace(runId)) return new AgentSteerOutcome(AgentSteerAttempt.NotSteerable);
        var steerId = AgentSteerLimits.NewSteerId();
        try
        {
            await _client.SteerAsync(runId, steerId, text, cancellationToken).ConfigureAwait(false);
            lock (_steerGate) _sentSteers.Add((runId, steerId));
            return new AgentSteerOutcome(AgentSteerAttempt.Sent, SteerId: steerId);
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

    /// <summary>Whether this window still tracks a steer it sent into <paramref name="runId"/>.</summary>
    public bool HasTrackedSteers(string runId)
    {
        lock (_steerGate) return _sentSteers.Any(item => string.Equals(item.RunId, runId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Steers this window sent into <paramref name="runId"/> that the run ended without
    /// reading. Only that run is read, so a steer is never sent into another session's chat.
    /// Settled steers (delivered or returned) are forgotten; pending ones stay tracked, as
    /// they are while a stopped run is still settling.
    /// </summary>
    public async Task<IReadOnlyList<AgentReturnedSteer>> TakeReturnedSteersAsync(
        string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        // Stop and the turn's completion can both ask; one of them takes each returned steer.
        await _returnedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string[] tracked;
            lock (_steerGate)
            {
                tracked = _sentSteers.Where(item => string.Equals(item.RunId, runId, StringComparison.Ordinal))
                    .Select(item => item.SteerId).ToArray();
            }

            if (tracked.Length == 0) return Array.Empty<AgentReturnedSteer>();
            AgentSteerListResponse listed;
            try
            {
                listed = await _client.ListSteersAsync(runId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is AgentApiException or System.Net.Http.HttpRequestException)
            {
                return Array.Empty<AgentReturnedSteer>();
            }

            var returned = new List<AgentReturnedSteer>();
            var settled = new HashSet<string>(StringComparer.Ordinal);
            foreach (var steer in listed.Steers)
            {
                if (steer.State == AgentSteerLimits.StatePending || !tracked.Contains(steer.SteerId)) continue;
                settled.Add(steer.SteerId);
                if (steer.State == AgentSteerLimits.StateReturned) returned.Add(new AgentReturnedSteer(steer.SteerId, steer.Text));
            }

            lock (_steerGate) _sentSteers.RemoveAll(item => settled.Contains(item.SteerId));
            return returned;
        }
        finally
        {
            _returnedGate.Release();
        }
    }
}
