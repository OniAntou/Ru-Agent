using RuAgent.Protocol.Subagents;

namespace RuApp.Services;

public interface ISubagentClient
{
    Task<SubagentListResponse> ListSubagentsAsync(string root, CancellationToken ct);
    Task<SubagentDetailResponse> GetSubagentAsync(string root, string agent, long? version, int offset, CancellationToken ct);
    /// <summary>A child attempt's activity rows and narration, read like any run's replay; observation only.</summary>
    Task<RuAgent.Client.AgentRunActivityReplay> ListChildActivityAsync(string runId, CancellationToken ct);
}
