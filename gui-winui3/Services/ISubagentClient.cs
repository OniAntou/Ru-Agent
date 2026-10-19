using RuAgent.Protocol.Subagents;

namespace RuApp.Services;

public interface ISubagentClient
{
    Task<SubagentListResponse> ListSubagentsAsync(string root, CancellationToken ct);
    Task<SubagentDetailResponse> GetSubagentAsync(string root, string agent, long? version, int offset, CancellationToken ct);
}
