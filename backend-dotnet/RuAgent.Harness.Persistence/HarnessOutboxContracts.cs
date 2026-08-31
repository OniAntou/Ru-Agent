using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Persistence;

public sealed record OutboxClaim(
    EventId IntentId,
    RunId RunId,
    SessionId SessionId,
    PayloadId PayloadId,
    ConversationOutboxRole Role,
    HistoryWaitKind HistoryWaitKind,
    int AttemptCount,
    string ClaimToken,
    DateTimeOffset LeaseExpiresAt);

public enum OutboxMutationOutcome
{
    Acknowledged,
    AlreadyAcknowledged,
    RetryScheduled,
    Failed,
    Conflict,
    NotFound,
}

public readonly record struct OutboxMutationResult(
    OutboxMutationOutcome Outcome,
    DateTimeOffset? NextAttemptAt = null);

public enum OutboxRunTransitionKind
{
    AcknowledgeHistory,
    FailRun,
}

public sealed record OutboxRunTransition(
    EventId IntentId,
    RunId RunId,
    HistoryWaitKind HistoryWaitKind,
    OutboxRunTransitionKind Kind,
    string? ErrorCode);

public abstract record OutboxWork
{
    public sealed record Idle : OutboxWork;
    public sealed record Transition(OutboxRunTransition Value) : OutboxWork;
    public sealed record Delivery(OutboxClaim Value) : OutboxWork;
}
