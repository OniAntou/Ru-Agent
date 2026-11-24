using RuAgent.Harness.Core.Persistence;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Core.Steering;

/// <summary>
/// The steer contract (ADR 0056): a message the user sends into a root run that is still
/// working. It waits durably until the run reaches a step boundary, then enters the model
/// context as a user entry, never between a tool call and its result.
/// </summary>
public static class RunSteerContract
{
    /// <summary>A steer is guidance, not a new prompt: one activity row carries it whole.</summary>
    public const int MaxCharacters = AgentSteerLimits.MaxCharacters;

    /// <summary>Steers waiting at once for one run. Past this the user is writing a new turn.</summary>
    public const int MaxPending = AgentSteerLimits.MaxPending;

    public const string IdPrefix = AgentSteerLimits.IdPrefix;

    /// <summary>Safe code of the <c>run.note_added</c> event that delivers one steer.</summary>
    public const string DeliveredSafeCode = AgentSteerLimits.DeliveredSafeCode;

    public const string StatePending = AgentSteerLimits.StatePending;
    public const string StateDelivered = AgentSteerLimits.StateDelivered;

    /// <summary>The run ended before the steer reached a boundary; the client offers it again.</summary>
    public const string StateReturned = AgentSteerLimits.StateReturned;

    public static bool IsValidId(string? steerId) => AgentSteerLimits.IsValidSteerId(steerId);

    /// <summary>
    /// The payload that delivers a steer shares its hex, so a client that sent the steer can
    /// match the <c>run.note_added</c> event to it without another read.
    /// </summary>
    public static PayloadId DeliveredPayloadId(string steerId) =>
        new(AgentSteerLimits.DeliveredPayloadId(steerId));

    /// <summary>
    /// States that still reach another model step. A run waiting on trust or history has not
    /// started stepping, and a terminal run never will.
    /// </summary>
    public static bool AcceptsSteer(HarnessRunState state) =>
        state is HarnessRunState.Running
            or HarnessRunState.WaitingApproval
            or HarnessRunState.WaitingUser
            or HarnessRunState.WaitingSubagents;
}

public sealed record DeliveredSteer(string SteerId, string Text);

/// <summary>Durable steers of a root run, read by the coordinator at step boundaries.</summary>
public interface IRunSteerInbox
{
    /// <summary>
    /// Delivers the run's pending steers when the run is at a step boundary, in arrival
    /// order, and returns them; returns none when the run is mid-step or has none.
    /// </summary>
    Task<IReadOnlyList<DeliveredSteer>> DeliverAsync(RunId runId, DateTimeOffset now, CancellationToken ct);

    Task<bool> HasPendingAsync(RunId runId, CancellationToken ct);
}

public static class RunSteerDelivery
{
    /// <summary>
    /// One <c>run.note_added</c> event per steer, each with a user-prompt payload the model
    /// context binds as a user entry at that event's sequence.
    /// </summary>
    public static HarnessCommit CreateCommit(AgentRun run, IReadOnlyList<DeliveredSteer> steers, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(steers);
        if (run.State != HarnessRunState.Running || steers.Count is < 1 or > RunSteerContract.MaxPending)
            throw new ArgumentException("run_steer_boundary_invalid");
        var events = new List<HarnessEventIntent>(steers.Count);
        var payloads = new List<HarnessPayloadIntent>(steers.Count);
        foreach (var steer in steers)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(steer.Text);
            var payloadId = RunSteerContract.DeliveredPayloadId(steer.SteerId);
            payloads.Add(new HarnessPayloadIntent(payloadId, HarnessPayloadKind.UserPrompt,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Utf8Text, Math.Max(bytes.Length, 1), bytes));
            events.Add(new HarnessEventIntent(new EventId("evt_" + Guid.NewGuid().ToString("N")),
                HarnessEventKind.RunNoteAdded, payloadId: payloadId, safeCode: RunSteerContract.DeliveredSafeCode));
        }

        var next = run.Advance(run.Status, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow,
            events.Count, now < run.UpdatedAt ? run.UpdatedAt : now);
        return new HarnessCommit(run.RunId, run.Version, next, events, payloads, []);
    }
}
