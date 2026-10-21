namespace RuAgent.Harness.Core.Subagents;

/// <summary>Durable, idempotent reservations made before model or tool batch dispatch.</summary>
public interface IAgentFamilyBudget
{
    Task<bool> ReserveModelAsync(AgentRun run, ModelStepId step, CancellationToken ct);
    /// <summary>Text-only compaction may run while a nonterminal run is parked; it still spends family budget.</summary>
    Task<bool> ReserveCheckpointAsync(AgentRun run, ModelStepId step, CancellationToken ct) => ReserveModelAsync(run, step, ct);
    Task<bool> ReserveToolsAsync(AgentRun run, ModelStepId step, int count, CancellationToken ct);
}
