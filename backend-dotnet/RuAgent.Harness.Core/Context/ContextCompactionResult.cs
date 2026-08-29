namespace RuAgent.Harness.Core.Context;

public enum ContextCompactionOutcome
{
    Committed,
    NoOp,
    CommitConflict,
    RunNotFound,
    NotAvailable,
}

public sealed record ContextCompactionResult(
    ContextCompactionOutcome Outcome,
    AgentRun? Run,
    ContextCompactionStrategy Strategy,
    string? CompactionId = null,
    long? BeforeTokens = null,
    long? AfterTokens = null,
    string? ErrorCode = null)
{
    public static ContextCompactionResult NoOp(
        AgentRun run,
        ContextCompactionStrategy strategy) =>
        new(ContextCompactionOutcome.NoOp, run, strategy);

    public static ContextCompactionResult NotAvailable(
        AgentRun run,
        ContextCompactionStrategy strategy) =>
        new(
            ContextCompactionOutcome.NotAvailable,
            run,
            strategy,
            ErrorCode: "compact_not_available");
}
