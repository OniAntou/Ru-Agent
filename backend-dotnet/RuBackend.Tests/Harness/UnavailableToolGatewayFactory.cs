extern alias Broker;

using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuBackend.Harness.Broker;

namespace RuBackend.Tests.Harness;

internal sealed class UnavailableToolGatewayFactory : IToolGatewayFactory
{
    public bool IsConfigured => false;
    public Task<IToolGateway> CreateAsync(string workspaceRoot, PolicySnapshot policySnapshot,
        RunId runId, string? agentSessionId, CancellationToken cancellationToken) =>
        Task.FromException<IToolGateway>(new InvalidOperationException("test_gateway_unavailable"));
    public Task<System.Text.Json.Nodes.JsonObject> ListSessionTasksAsync(string runId, string? killExecutionId,
        CancellationToken cancellationToken) =>
        Task.FromException<System.Text.Json.Nodes.JsonObject>(new InvalidOperationException("test_gateway_unavailable"));
    public Task<(bool Authorized, string? AuthorizationToken, Broker::RuAgent.Broker.Policy.ApprovalRememberPlan? RememberPlan)> RespondApprovalAsync(string runId, string hostApprovalId,
        string command, CancellationToken cancellationToken) =>
        Task.FromException<(bool Authorized, string? AuthorizationToken, Broker::RuAgent.Broker.Policy.ApprovalRememberPlan? RememberPlan)>(new InvalidOperationException("test_gateway_unavailable"));
    public Task<bool> RevokeSessionGrantsAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromException<bool>(new InvalidOperationException("test_gateway_unavailable"));
    public Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CloseRunAsync(string runId) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
