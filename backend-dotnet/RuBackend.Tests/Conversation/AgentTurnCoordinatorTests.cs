using System.Text.Json.Nodes;
using RuBackend.Agent;
using RuBackend.Conversation;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Conversation;

public sealed class AgentTurnCoordinatorTests
{
    [Fact]
    public async Task Volume_tool_gets_the_reversible_system_write_scope()
    {
        var broker = new FakeBroker();
        var result = await new AgentTurnCoordinator(broker).ExecuteToolAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal("system.write", broker.LastRequiredScope);
        Assert.Contains("system.write", broker.OpenedScopes);
    }

    [Fact]
    public async Task Volume_tool_is_denied_when_the_broker_grant_lacks_system_write()
    {
        var broker = new FakeBroker(
            omittedScopes: new HashSet<string>(["system.write"], StringComparer.Ordinal));
        var result = await new AgentTurnCoordinator(broker).ExecuteToolAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal("denied", result.Status);
        Assert.Equal("scope_missing", result.ErrorCode);
        Assert.Equal("system.write", broker.LastRequiredScope);
    }

    private static AgentTurnRequest Request() => new(
        new WorkerToolRequest(
            "tool-request-1",
            "session-1",
            "correlation-1",
            "system.volume",
            "1.0.0",
            new JsonObject
            {
                ["operation"] = "set",
                ["level"] = 2,
            },
            30_000));

    private sealed class FakeBroker : IAgentBrokerClient
    {
        private readonly IReadOnlySet<string> _omittedScopes;

        public FakeBroker(IReadOnlySet<string>? omittedScopes = null)
        {
            _omittedScopes = omittedScopes ?? new HashSet<string>(StringComparer.Ordinal);
        }

        public IReadOnlySet<string> OpenedScopes { get; private set; } = new HashSet<string>(StringComparer.Ordinal);
        public string? LastRequiredScope { get; private set; }

        public Task<AgentSessionGrant> OpenSessionAsync(
            string sessionId,
            IReadOnlySet<string> scopes,
            IReadOnlySet<string> skillIds,
            string? correlationId,
            int deadlineMs,
            CancellationToken cancellationToken)
        {
            OpenedScopes = scopes
                .Where(scope => !_omittedScopes.Contains(scope))
                .ToHashSet(StringComparer.Ordinal);
            return Task.FromResult(
                new AgentSessionGrant(
                    sessionId,
                    OpenedScopes,
                    skillIds,
                    DateTimeOffset.UtcNow.AddMinutes(1)));
        }

        public Task<AgentToolResult> ExecuteToolAsync(
            string sessionId,
            string toolId,
            string toolVersion,
            JsonObject arguments,
            string requiredScope,
            string? correlationId,
            int deadlineMs,
            CancellationToken cancellationToken)
        {
            LastRequiredScope = requiredScope;
            if (!OpenedScopes.Contains(requiredScope))
            {
                return Task.FromResult(new AgentToolResult(
                    "tool-request-1",
                    "denied",
                    new JsonObject(),
                    "scope_missing",
                    "Tool scope is not granted."));
            }

            return Task.FromResult(new AgentToolResult(
                "tool-request-1",
                "succeeded",
                new JsonObject
                {
                    ["operation"] = "set",
                    ["level"] = 2,
                    ["muted"] = false,
                    ["verified"] = true,
                },
                null,
                "Volume set verified."));
        }

        public Task<AgentToolResult> RunSkillAsync(
            string sessionId,
            string skillId,
            JsonObject input,
            string? correlationId,
            int deadlineMs,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentToolResult> RespondToApprovalAsync(
            AgentApprovalResponse response,
            string? correlationId,
            int deadlineMs,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CancelAsync(
            string sessionId,
            string requestId,
            string? correlationId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CloseSessionAsync(
            string sessionId,
            string? correlationId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
