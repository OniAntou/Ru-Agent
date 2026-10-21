using System.Text.Json;

namespace RuApp.Services;

/// <summary>
/// The remembered approval rules the Host keeps ("Always allow"). Listing is read-only;
/// removing a rule only narrows what runs without asking.
/// </summary>
public interface IApprovalRulesClient
{
    Task<IReadOnlyList<JsonElement>> ListPoliciesAsync(string? scope, string? workspaceId, CancellationToken cancellationToken);

    Task DeletePolicyAsync(string ruleId, CancellationToken cancellationToken);
}
