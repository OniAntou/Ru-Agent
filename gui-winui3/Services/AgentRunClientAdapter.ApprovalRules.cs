using System.Text.Json;

namespace RuApp.Services;

public sealed partial class AgentRunClientAdapter : IApprovalRulesClient
{
    public Task<IReadOnlyList<JsonElement>> ListPoliciesAsync(string? scope, string? workspaceId, CancellationToken cancellationToken) =>
        _apiClient.ListPoliciesAsync(scope, workspaceId, cancellationToken);

    /// <summary>The user removed the rule in Settings; the delete is the interactive confirmation.</summary>
    public Task DeletePolicyAsync(string ruleId, CancellationToken cancellationToken) =>
        _apiClient.DeletePolicyAsync(ruleId, interactive: true, cancellationToken);
}
