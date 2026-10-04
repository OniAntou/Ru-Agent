using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

internal static class ChatRunWorkflow
{
    public const int MaxApprovalReplayEvents = 2048;
    public const int MaxApprovalReplayFrameChars = 64 * 1024;
    private static readonly TimeSpan ApprovalReplayTimeout = TimeSpan.FromSeconds(4);

    public static bool IsFollowable(GetAgentRunResponse run) =>
        run.TerminalAt is null
        && (string.Equals(run.Status, "running", StringComparison.OrdinalIgnoreCase)
            || string.Equals(run.Status, "waiting_approval", StringComparison.OrdinalIgnoreCase));

    public static bool SameSnapshot(GetAgentRunResponse expected, GetAgentRunResponse actual) =>
        string.Equals(expected.RunId, actual.RunId, StringComparison.Ordinal)
        && string.Equals(expected.SessionId, actual.SessionId, StringComparison.Ordinal)
        && string.Equals(expected.Status, actual.Status, StringComparison.OrdinalIgnoreCase)
        && expected.Version == actual.Version
        && expected.LastSequence == actual.LastSequence;

    public static async Task<string?> FindNewestFollowableRunIdAsync(
        AgentApiClient client,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var runIds = await client.ListSessionRunIdsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        foreach (var runId in runIds.TakeLast(100).Reverse())
        {
            var run = await client.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (IsFollowable(run))
            {
                return runId;
            }
        }

        return null;
    }

    public static async Task<(HarnessSafeEventEnvelope? Pending, string? Error)> ReadPendingApprovalAsync(
        AgentSseClient sseClient,
        GetAgentRunResponse snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sseClient);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(snapshot.Status, "waiting_approval", StringComparison.OrdinalIgnoreCase)
            || snapshot.LastSequence <= 0)
        {
            return (null, "approval_recovery_unavailable: run summary has no pending approval cursor");
        }

        using var replayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        replayCancellation.CancelAfter(ApprovalReplayTimeout);
        HarnessSafeEventEnvelope? latestApproval = null;
        var eventCount = 0;
        var reachedCursor = false;

        try
        {
            var afterSequence = Math.Max(0, snapshot.LastSequence - MaxApprovalReplayEvents);
            await foreach (var frame in sseClient.ReadEventsAsync(snapshot.RunId, afterSequence, replayCancellation.Token).ConfigureAwait(false))
            {
                if (frame.IsTransient || frame.DurableSequence is not { } sequence)
                {
                    continue;
                }

                if (sequence > snapshot.LastSequence)
                {
                    reachedCursor = true;
                    break;
                }

                if (++eventCount > MaxApprovalReplayEvents)
                {
                    return (null, "approval_recovery_incomplete: durable event replay exceeded its bounded limit");
                }

                if (frame.Data.Length > MaxApprovalReplayFrameChars)
                {
                    return (null, "approval_recovery_incomplete: durable event exceeded its bounded frame limit");
                }

                if (frame.Data.Length > 0
                    && frame.Data.Contains("\"approval.requested\"", StringComparison.Ordinal))
                {
                    try
                    {
                        var envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
                        if (envelope is { EventType: "approval.requested" }
                            && envelope.Sequence == sequence
                            && string.Equals(envelope.RunId, snapshot.RunId, StringComparison.Ordinal)
                            && !string.IsNullOrWhiteSpace(envelope.ApprovalId))
                        {
                            latestApproval = envelope;
                        }
                    }
                    catch (JsonException)
                    {
                        return (null, "approval_recovery_incomplete: durable approval event was invalid");
                    }
                }

                if (sequence == snapshot.LastSequence)
                {
                    reachedCursor = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "approval_recovery_timeout: durable approval events did not reach the run summary cursor");
        }

        return reachedCursor && latestApproval is not null
            ? (latestApproval, null)
            : (null, "approval_recovery_incomplete: no pending approval was found at the run summary cursor");
    }
}
