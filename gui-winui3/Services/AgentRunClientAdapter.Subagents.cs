using RuAgent.Protocol.Subagents;

namespace RuApp.Services;

public sealed partial class AgentRunClientAdapter : ISubagentClient
{
    public Task<SubagentListResponse> ListSubagentsAsync(string root, CancellationToken ct) => _apiClient.ListSubagentsAsync(root, ct);
    public Task<SubagentDetailResponse> GetSubagentAsync(string root, string agent, long? version, int offset, CancellationToken ct) =>
        _apiClient.GetSubagentAsync(root, agent, version, offset, ct: ct);
}
