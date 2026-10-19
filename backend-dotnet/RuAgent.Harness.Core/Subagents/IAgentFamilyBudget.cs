namespace RuAgent.Harness.Core.Subagents;

/// <summary>Durable, idempotent reservations made before model or tool batch dispatch.</summary>
public interface IAgentFamilyBudget
{
    Task<bool> ReserveModelAsync(AgentRun run, ModelStepId step, CancellationToken ct);
    Task<bool> ReserveToolsAsync(AgentRun run, ModelStepId step, int count, CancellationToken ct);
}
