using System.Collections.ObjectModel;

namespace RuAgent.Harness.Core.Persistence;

public sealed record NewAgentRun
{
    public NewAgentRun(
        AgentRun run,
        IReadOnlyList<HarnessEventIntent> events,
        IReadOnlyList<HarnessPayloadIntent> payloads,
        IReadOnlyList<ConversationOutboxIntent> conversationOutbox,
        RunCreationIdentity creationIdentity,
        RunProviderIntent? providerIntent = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(conversationOutbox);
        ArgumentNullException.ThrowIfNull(creationIdentity);
        Run = run;
        Events = new ReadOnlyCollection<HarnessEventIntent>(events.ToArray());
        Payloads = new ReadOnlyCollection<HarnessPayloadIntent>(payloads.ToArray());
        ConversationOutbox = new ReadOnlyCollection<ConversationOutboxIntent>(
            conversationOutbox.ToArray());
        CreationIdentity = creationIdentity;
        ProviderIntent = providerIntent;
    }

    public AgentRun Run { get; }
    public IReadOnlyList<HarnessEventIntent> Events { get; }
    public IReadOnlyList<HarnessPayloadIntent> Payloads { get; }
    public IReadOnlyList<ConversationOutboxIntent> ConversationOutbox { get; }
    public RunCreationIdentity CreationIdentity { get; }
    public RunProviderIntent? ProviderIntent { get; }
}

public sealed record RunCreationIdentity
{
    public RunCreationIdentity(
        string idempotencyKeySha256,
        string normalizedRequestSha256,
        long sessionPresetVersion)
    {
        IdempotencyKeySha256 = RequireSha256(
            idempotencyKeySha256,
            nameof(idempotencyKeySha256));
        NormalizedRequestSha256 = RequireSha256(
            normalizedRequestSha256,
            nameof(normalizedRequestSha256));
        if (sessionPresetVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionPresetVersion));
        }

        SessionPresetVersion = sessionPresetVersion;
    }

    public string IdempotencyKeySha256 { get; }

    public string NormalizedRequestSha256 { get; }

    public long SessionPresetVersion { get; }

    private static string RequireSha256(string? value, string parameterName)
    {
        if (value is not { Length: 64 }
            || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Hash must contain exactly 64 lowercase hexadecimal characters.",
                parameterName);
        }

        return value;
    }
}

public enum CreateRunOutcome
{
    Created,
    AlreadyExists,
    Conflict,
}

public readonly record struct CreateRunResult(
    CreateRunOutcome Outcome,
    AgentRun Run,
    string? ErrorCode = null)
{
    public static CreateRunResult Created(AgentRun run) =>
        new(CreateRunOutcome.Created, run);

    public static CreateRunResult AlreadyExists(AgentRun run) =>
        new(CreateRunOutcome.AlreadyExists, run);

    public static CreateRunResult Conflict(AgentRun run, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode) || errorCode.Length > 64)
        {
            throw new ArgumentException("Conflict code must be bounded.", nameof(errorCode));
        }

        return new(CreateRunOutcome.Conflict, run, errorCode);
    }
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
