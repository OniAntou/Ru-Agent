using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RuAgent.Client;

public interface IAgentPolicyClient
{
    Task<IReadOnlyList<JsonElement>> ListPoliciesAsync(string? scope = null, string? workspaceId = null, CancellationToken cancellationToken = default);
    Task<JsonElement> CreatePolicyAsync(object policyBody, bool interactive, CancellationToken cancellationToken = default);
    Task DeletePolicyAsync(string ruleId, bool interactive, CancellationToken cancellationToken = default);
    Task<JsonElement> SetSessionPresetAsync(string sessionId, string preset, long expectedVersion, bool interactive, CancellationToken cancellationToken = default, string? snapshotToken = null);
    Task<JsonElement> RestoreSessionPresetAsync(string sessionId, string snapshotToken, CancellationToken cancellationToken = default);
}
