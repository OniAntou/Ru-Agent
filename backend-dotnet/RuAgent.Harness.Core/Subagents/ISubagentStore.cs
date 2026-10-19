using RuAgent.Harness.Core.Persistence;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Core.Subagents;

public sealed record SubagentCreateCommand(string AgentId, RunId RootRunId, string InvocationId, SubagentRole Role, NewAgentRun ChildRun);
public sealed record SubagentRecord(string AgentId, RunId RootRunId, SessionId InternalSessionId, SubagentRole Role, bool Canceled);
public enum SubagentBudgetKind { ModelStep, ToolCall }
public sealed record SubagentAttempt(SubagentRecord Agent, AgentRun Run, int AttemptNumber,
    DateTimeOffset CreatedAt, DateTimeOffset? AdmittedAt, DateTimeOffset? DeadlineAt);

public interface ISubagentStore
{
    Task<SubagentRecord> CreateAsync(SubagentCreateCommand command, CancellationToken ct);
    Task<IReadOnlyList<SubagentRecord>> ListAsync(RunId rootRunId, CancellationToken ct);
    Task<bool> IsInternalSessionAsync(SessionId sessionId, CancellationToken ct);
    Task CancelFamilyAsync(RunId rootRunId, CancellationToken ct);
    Task<IReadOnlyList<RunId>> CancelAsync(RunId rootRunId, string agentId, CancellationToken ct);
}
