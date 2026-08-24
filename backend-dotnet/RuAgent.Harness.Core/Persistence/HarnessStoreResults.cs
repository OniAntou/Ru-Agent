using System.Collections.ObjectModel;

namespace RuAgent.Harness.Core.Persistence;

public sealed record NewAgentRun
{
    public NewAgentRun(
        AgentRun run,
        IReadOnlyList<HarnessEventIntent> events,
        IReadOnlyList<HarnessPayloadIntent> payloads,
        IReadOnlyList<ConversationOutboxIntent> conversationOutbox)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(conversationOutbox);
        Run = run;
        Events = new ReadOnlyCollection<HarnessEventIntent>(events.ToArray());
        Payloads = new ReadOnlyCollection<HarnessPayloadIntent>(payloads.ToArray());
        ConversationOutbox = new ReadOnlyCollection<ConversationOutboxIntent>(
            conversationOutbox.ToArray());
    }

    public AgentRun Run { get; }
    public IReadOnlyList<HarnessEventIntent> Events { get; }
    public IReadOnlyList<HarnessPayloadIntent> Payloads { get; }
    public IReadOnlyList<ConversationOutboxIntent> ConversationOutbox { get; }
}

public enum CreateRunOutcome
{
    Created,
    AlreadyExists,
    Conflict,
}

public readonly record struct CreateRunResult(CreateRunOutcome Outcome, AgentRun Run)
{
    public static CreateRunResult Created(AgentRun run) =>
        new(CreateRunOutcome.Created, run);

    public static CreateRunResult AlreadyExists(AgentRun run) =>
        new(CreateRunOutcome.AlreadyExists, run);
}

public enum HarnessCommitOutcome
{
    Committed,
    VersionConflict,
    RunNotFound,
}

public readonly record struct HarnessCommitResult(
    HarnessCommitOutcome Outcome,
    AgentRun? Run)
{
    public static HarnessCommitResult Committed(AgentRun run) =>
        new(HarnessCommitOutcome.Committed, run);

    public static HarnessCommitResult VersionConflict(AgentRun current) =>
        new(HarnessCommitOutcome.VersionConflict, current);

    public static HarnessCommitResult RunNotFound() =>
        new(HarnessCommitOutcome.RunNotFound, null);
}

public sealed record HarnessEventEnvelope(int ProtocolVersion, HarnessEvent Event);
