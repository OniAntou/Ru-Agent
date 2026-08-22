using System.Text.Json.Nodes;
using RuAgent.Protocol;
using RuBackend.Agent;
using RuBackend.Conversation;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Conversation;

public sealed class AgentTurnCoordinatorTests
{
    [Fact]
    public async Task Volume_tool_uses_the_exact_capability_and_preserves_the_verified_receipt()
    {
        var broker = new FakeBroker();
        var result = await new AgentTurnCoordinator(broker).ExecuteToolAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
        Assert.Equal(new[] { "system.write" }, broker.LastRequestedScopes);
        Assert.Empty(broker.LastRequestedSkillIds);
        Assert.Equal("native_mutation", broker.LastCall!.CapabilityClass);
        Assert.Equal(ToolVerificationStates.Verified, result.VerificationState);
        Assert.Equal("default_render_multimedia", result.EndpointRole);
        Assert.Equal("0123456789abcdef", result.EndpointHash);
    }

    [Theory]
    [InlineData("missing.tool", "1.0.0")]
    [InlineData("system.volume", "2.0.0")]
    public async Task Unknown_exact_binding_fails_without_opening_a_session(
        string toolId,
        string toolVersion)
    {
        var broker = new FakeBroker();
        var result = await new AgentTurnCoordinator(broker).ExecuteToolAsync(
            Request(toolId: toolId, toolVersion: toolVersion),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Failed, result.Status);
        Assert.Equal("capability_unavailable", result.ErrorCode);
        Assert.Equal(0, broker.OpenCount);
        Assert.Equal(0, broker.ExecuteCount);
    }

    [Fact]
    public async Task Capability_class_mismatch_fails_without_opening_a_session()
    {
        var broker = new FakeBroker();
        var result = await new AgentTurnCoordinator(broker).ExecuteToolAsync(
            Request(capabilityClass: "internal"),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Failed, result.Status);
        Assert.Equal("capability_mismatch", result.ErrorCode);
        Assert.Equal(0, broker.OpenCount);
        Assert.Equal(0, broker.ExecuteCount);
    }

    [Fact]
    public async Task Newly_opened_session_emits_only_the_actual_grant()
    {
        var broker = new FakeBroker
        {
            ActualScopes = new HashSet<string>(["system.write"], StringComparer.Ordinal),
        };
        var events = await CollectAsync(
            new AgentTurnCoordinator(broker).HandleToolTurnAsync(
                Request(),
                CancellationToken.None));

        var sessionEvent = Assert.Single(events, item => item.Type == "agent.session");
        Assert.Equal("session-1", sessionEvent.SessionId);
        Assert.Equal(new[] { "system.write" }, sessionEvent.Scopes);
        Assert.Equal(1, events.Count(item => item.Type == "agent.activity" && item.Status == "requested"));
        Assert.Equal(1, events.Count(item => item.Type == "agent.activity" && item.Status == "succeeded"));
        Assert.All(events.Where(item => item.Type == "agent.activity"), item => Assert.Null(item.Content));
    }

    [Fact]
    public async Task Mismatched_receipt_request_id_fails_closed()
    {
        var broker = new FakeBroker { ResultRequestId = "different-request" };
        var result = await new AgentTurnCoordinator(broker).ExecuteToolAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Failed, result.Status);
        Assert.Equal("receipt_mismatch", result.ErrorCode);
        Assert.Equal(ToolVerificationStates.Failed, result.VerificationState);
        Assert.Null(result.EndpointRole);
        Assert.Null(result.EndpointHash);
    }

    [Fact]
    public async Task Late_result_after_cancellation_is_not_yielded_as_completed_activity()
    {
        var broker = new FakeBroker { BlockExecution = true };
        using var cancellation = new CancellationTokenSource();
        var collecting = CollectUntilCancelledAsync(
            new AgentTurnCoordinator(broker).HandleToolTurnAsync(Request(), cancellation.Token),
            cancellation.Token);
        await broker.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        broker.ReleaseExecution.TrySetResult();
        var events = await collecting;

        Assert.DoesNotContain(
            events,
            item => item.Type == "agent.activity" && item.Status == ToolResultStatuses.Succeeded);
    }

    private static AgentTurnRequest Request(
        string toolId = "system.volume",
        string toolVersion = "1.0.0",
        string capabilityClass = "native_mutation") => new(
        new WorkerToolRequest(
            "tool-request-1",
            "session-1",
            "correlation-1",
            toolId,
            toolVersion,
            capabilityClass,
            new JsonObject
            {
                ["operation"] = "set",
                ["level"] = 2,
            },
            30_000));

    private static async Task<List<ChatEvent>> CollectAsync(IAsyncEnumerable<ChatEvent> source)
    {
        var events = new List<ChatEvent>();
        await foreach (var item in source)
        {
            events.Add(item);
        }

        return events;
    }

    private static async Task<List<ChatEvent>> CollectUntilCancelledAsync(
        IAsyncEnumerable<ChatEvent> source,
        CancellationToken cancellationToken)
    {
        var events = new List<ChatEvent>();
        try
        {
            await foreach (var item in source.WithCancellation(cancellationToken))
            {
                events.Add(item);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        return events;
    }

    private sealed class FakeBroker : IAgentBrokerClient
    {
        public IReadOnlySet<string> ActualScopes { get; init; } =
            new HashSet<string>(["system.write"], StringComparer.Ordinal);
        public string ResultRequestId { get; init; } = "tool-request-1";
        public bool BlockExecution { get; init; }
        public TaskCompletionSource ExecutionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseExecution { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int OpenCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public IReadOnlySet<string> LastRequestedScopes { get; private set; } =
            new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlySet<string> LastRequestedSkillIds { get; private set; } =
            new HashSet<string>(StringComparer.Ordinal);
        public AgentToolCall? LastCall { get; private set; }

        public Task<IReadOnlyList<AgentToolCapability>> GetCapabilitiesAsync(
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AgentToolCapability>>(
            [new AgentToolCapability(
                "system.volume",
                "1.0.0",
                new HashSet<string>(["system.write"], StringComparer.Ordinal),
                "reversible",
                "native_pending",
                true)]);

        public Task<AgentSessionGrant> OpenSessionAsync(
            string sessionId,
            IReadOnlySet<string> scopes,
            IReadOnlySet<string> skillIds,
            string? correlationId,
            int deadlineMs,
            CancellationToken cancellationToken)
        {
            OpenCount++;
            LastRequestedScopes = scopes;
            LastRequestedSkillIds = skillIds;
            return Task.FromResult(new AgentSessionGrant(
                sessionId,
                ActualScopes,
                new HashSet<string>(StringComparer.Ordinal),
                DateTimeOffset.UtcNow.AddMinutes(1)));
        }

        public async Task<AgentToolResult> ExecuteToolAsync(
            AgentToolCall call,
            CancellationToken cancellationToken)
        {
            ExecuteCount++;
            LastCall = call;
            ExecutionStarted.TrySetResult();
            if (BlockExecution)
            {
                await ReleaseExecution.Task.ConfigureAwait(false);
            }

            return new AgentToolResult(
                ResultRequestId,
                ToolResultStatuses.Succeeded,
                new JsonObject { ["operation"] = "set", ["level"] = 2 },
                null,
                "Volume set verified.",
                12,
                ToolVerificationStates.Verified,
                "default_render_multimedia",
                "0123456789abcdef",
                true);
        }

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
