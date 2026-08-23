using System.Text.Json.Nodes;
using RuAgent.Protocol;
using RuBackend.Agent;
using RuBackend.Conversation;
using RuBackend.Diagnostics;
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

    [Fact]
    public async Task Verified_adapter_receipt_emits_one_correlated_truthful_sequence()
    {
        var diagnostics = new RecordingEmitter();
        var result = await new AgentTurnCoordinator(
            new FakeBroker(),
            diagnostics: diagnostics).ExecuteToolAsync(Request(), CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
        Assert.Equal(
            new[]
            {
                DiagnosticCodes.ToolRequested,
                DiagnosticCodes.ToolAdapterStarted,
                DiagnosticCodes.ToolAdapterCompleted,
                DiagnosticCodes.ToolVerificationSucceeded,
            },
            diagnostics.Events.Select(item => item.EventCode));
        Assert.Single(diagnostics.Events.Select(item => item.CorrelationId).Distinct());
        Assert.All(diagnostics.Events, item =>
        {
            Assert.Equal("correlation-1", item.CorrelationId);
            Assert.Equal("system.volume", item.ToolId);
            Assert.Equal("1.0.0", item.ToolVersion);
        });
        var verified = diagnostics.Events[^1];
        Assert.Equal("verified", verified.VerificationState);
        Assert.Equal("default_render_multimedia", verified.EndpointRole);
        Assert.Equal("0123456789abcdef", verified.EndpointHash);
    }

    [Fact]
    public async Task Failed_readback_ends_in_verification_failed()
    {
        var diagnostics = new RecordingEmitter();
        var broker = new FakeBroker
        {
            ResultStatus = ToolResultStatuses.Failed,
            ResultErrorCode = "readback_mismatch",
            ResultSafeSummary = "Volume change could not be verified.",
            ResultVerificationState = ToolVerificationStates.Failed,
        };

        var result = await new AgentTurnCoordinator(
            broker,
            diagnostics: diagnostics).ExecuteToolAsync(Request(), CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Failed, result.Status);
        Assert.Equal(
            new[]
            {
                DiagnosticCodes.ToolRequested,
                DiagnosticCodes.ToolAdapterStarted,
                DiagnosticCodes.ToolAdapterCompleted,
                DiagnosticCodes.ToolVerificationFailed,
            },
            diagnostics.Events.Select(item => item.EventCode));
        Assert.Equal("readback_mismatch", diagnostics.Events[^1].ErrorCode);
        Assert.Equal("failed", diagnostics.Events[^1].VerificationState);
    }

    [Theory]
    [InlineData(ToolResultStatuses.Denied, ToolVerificationStates.Failed, DiagnosticCodes.ToolPolicyDenied)]
    [InlineData(ToolResultStatuses.ApprovalRequired, ToolVerificationStates.Pending, DiagnosticCodes.ToolApprovalRequired)]
    public async Task Predispatch_receipt_never_claims_adapter_execution(
        string status,
        string verificationState,
        string expectedEventCode)
    {
        var diagnostics = new RecordingEmitter();
        var broker = new FakeBroker
        {
            ResultStatus = status,
            ResultErrorCode = status,
            ResultSafeSummary = "Action did not dispatch.",
            ResultVerificationState = verificationState,
            ResultEndpointRole = null,
            ResultEndpointHash = null,
            AdapterInvoked = false,
        };

        _ = await new AgentTurnCoordinator(
            broker,
            diagnostics: diagnostics).ExecuteToolAsync(Request(), CancellationToken.None);

        Assert.Equal(
            new[] { DiagnosticCodes.ToolRequested, expectedEventCode },
            diagnostics.Events.Select(item => item.EventCode));
        Assert.DoesNotContain(diagnostics.Events, item =>
            item.EventCode is DiagnosticCodes.ToolAdapterStarted or DiagnosticCodes.ToolAdapterCompleted);
    }

    [Theory]
    [InlineData(ToolResultStatuses.Succeeded, ToolVerificationStates.Verified, false)]
    [InlineData(ToolResultStatuses.Denied, ToolVerificationStates.Failed, true)]
    [InlineData(ToolResultStatuses.ApprovalRequired, ToolVerificationStates.Pending, true)]
    public async Task Impossible_adapter_receipt_is_rejected(
        string status,
        string verificationState,
        bool adapterInvoked)
    {
        var diagnostics = new RecordingEmitter();
        var broker = new FakeBroker
        {
            ResultStatus = status,
            ResultErrorCode = status == ToolResultStatuses.Succeeded ? null : status,
            ResultVerificationState = verificationState,
            AdapterInvoked = adapterInvoked,
        };

        var result = await new AgentTurnCoordinator(
            broker,
            diagnostics: diagnostics).ExecuteToolAsync(Request(), CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Failed, result.Status);
        Assert.Equal("invalid_tool_receipt", result.ErrorCode);
        Assert.Equal(new[] { DiagnosticCodes.ToolRequested }, diagnostics.Events.Select(item => item.EventCode));
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

    [Fact]
    public async Task A_new_chat_session_closes_the_old_lease_before_opening()
    {
        var broker = new FakeBroker();
        var coordinator = new AgentTurnCoordinator(broker);

        var first = await coordinator.ExecuteToolAsync(
            Request(
                requestId: "tool-1",
                sessionId: "session-1",
                correlationId: "correlation-1"),
            CancellationToken.None);
        var second = await coordinator.ExecuteToolAsync(
            Request(
                requestId: "tool-2",
                sessionId: "session-2",
                correlationId: "correlation-2"),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Succeeded, first.Status);
        Assert.Equal(ToolResultStatuses.Succeeded, second.Status);
        Assert.Equal(
            new[]
            {
                "open:session-1",
                "execute:session-1",
                "close:session-1",
                "open:session-2",
                "execute:session-2",
            },
            broker.Calls);
    }

    [Fact]
    public async Task Repeated_tools_in_one_session_reuse_the_valid_lease()
    {
        var broker = new FakeBroker();
        var coordinator = new AgentTurnCoordinator(broker);

        _ = await coordinator.ExecuteToolAsync(
            Request(requestId: "tool-1", correlationId: "correlation-1"),
            CancellationToken.None);
        _ = await coordinator.ExecuteToolAsync(
            Request(requestId: "tool-2", correlationId: "correlation-2"),
            CancellationToken.None);

        Assert.Equal(1, broker.OpenCount);
        Assert.Equal(0, broker.CloseCount);
        Assert.Equal(
            new[] { "open:session-1", "execute:session-1", "execute:session-1" },
            broker.Calls);
    }

    [Fact]
    public async Task Failed_close_blocks_the_replacement_session_and_adapter()
    {
        var broker = new FakeBroker();
        var coordinator = new AgentTurnCoordinator(broker);
        _ = await coordinator.ExecuteToolAsync(Request(), CancellationToken.None);
        broker.CloseErrorCode = "broker_unavailable";

        var result = await coordinator.ExecuteToolAsync(
            Request(
                requestId: "tool-2",
                sessionId: "session-2",
                correlationId: "correlation-2"),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Failed, result.Status);
        Assert.Equal("broker_unavailable", result.ErrorCode);
        Assert.Equal("session-1", broker.ActiveSessionId);
        Assert.Equal(1, broker.OpenCount);
        Assert.Equal(1, broker.ExecuteCount);
    }

    [Fact]
    public async Task Incomplete_new_grant_is_closed_without_adapter_execution()
    {
        var broker = new FakeBroker
        {
            ActualScopes = new HashSet<string>(StringComparer.Ordinal),
        };

        var result = await new AgentTurnCoordinator(broker).ExecuteToolAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Failed, result.Status);
        Assert.Equal("scope_not_granted", result.ErrorCode);
        Assert.Null(broker.ActiveSessionId);
        Assert.Equal(0, broker.ExecuteCount);
        Assert.Equal(new[] { "open:session-1", "close:session-1" }, broker.Calls);
    }

    [Fact]
    public async Task Profile_session_requests_the_union_of_advertised_turn_scopes()
    {
        var broker = new FakeBroker
        {
            ExtraCapabilities =
            [
                new AgentToolCapability(
                    "files.write",
                    "1.0.0",
                    new HashSet<string>(["workspace.write"], StringComparer.Ordinal),
                    "reversible",
                    "executable",
                    true),
            ],
        };

        var result = await new AgentTurnCoordinator(broker).ExecuteToolAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
        Assert.Equal(
            new HashSet<string>(["system.write", "workspace.write"], StringComparer.Ordinal),
            broker.LastRequestedScopes);
    }

    [Fact]
    public async Task Cancelling_a_tool_releases_the_open_profile_session()
    {
        var broker = new FakeBroker { BlockExecution = true };
        var coordinator = new AgentTurnCoordinator(broker);
        using var cancellation = new CancellationTokenSource();
        var executing = coordinator.ExecuteToolAsync(Request(), cancellation.Token);
        await broker.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        broker.ReleaseExecution.TrySetResult();
        var result = await executing;

        Assert.Equal(ToolResultStatuses.Cancelled, result.Status);
        Assert.Null(broker.ActiveSessionId);
        Assert.Contains("close:session-1", broker.Calls);
    }

    private static AgentTurnRequest Request(
        string toolId = "system.volume",
        string toolVersion = "1.0.0",
        string capabilityClass = "native_mutation",
        string requestId = "tool-request-1",
        string sessionId = "session-1",
        string correlationId = "correlation-1",
        CapabilityProfile profile = CapabilityProfile.Agent) => new(
        new WorkerToolRequest(
            requestId,
            sessionId,
            correlationId,
            toolId,
            toolVersion,
            capabilityClass,
            new JsonObject
            {
                ["operation"] = "set",
                ["level"] = 2,
            },
            30_000),
        null,
        profile);

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
        public IReadOnlyList<AgentToolCapability> ExtraCapabilities { get; init; } = [];
        public string? ResultRequestId { get; init; }
        public string ResultStatus { get; init; } = ToolResultStatuses.Succeeded;
        public string? ResultErrorCode { get; init; }
        public string ResultSafeSummary { get; init; } = "Volume set verified.";
        public string ResultVerificationState { get; init; } = ToolVerificationStates.Verified;
        public string? ResultEndpointRole { get; init; } = "default_render_multimedia";
        public string? ResultEndpointHash { get; init; } = "0123456789abcdef";
        public bool AdapterInvoked { get; init; } = true;
        public bool BlockExecution { get; init; }
        public string? CloseErrorCode { get; set; }
        public string? ActiveSessionId { get; private set; }
        public TaskCompletionSource ExecutionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseExecution { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int OpenCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public int CloseCount { get; private set; }
        public List<string> Calls { get; } = [];
        public IReadOnlySet<string> LastRequestedScopes { get; private set; } =
            new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlySet<string> LastRequestedSkillIds { get; private set; } =
            new HashSet<string>(StringComparer.Ordinal);
        public AgentToolCall? LastCall { get; private set; }

        public Task<IReadOnlyList<AgentToolCapability>> GetCapabilitiesAsync(
            CapabilityProfile profile,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<AgentToolCapability> capabilities =
            [
                new AgentToolCapability(
                    "system.volume",
                    "1.0.0",
                    new HashSet<string>(["system.write"], StringComparer.Ordinal),
                    "reversible",
                    "native_pending",
                    true),
                ..ExtraCapabilities,
            ];
            return Task.FromResult(capabilities);
        }

        public Task<AgentSessionGrant> OpenSessionAsync(
            string sessionId,
            IReadOnlySet<string> scopes,
            IReadOnlySet<string> skillIds,
            string? correlationId,
            int deadlineMs,
            CancellationToken cancellationToken)
        {
            Calls.Add($"open:{sessionId}");
            if (ActiveSessionId is not null
                && !string.Equals(ActiveSessionId, sessionId, StringComparison.Ordinal))
            {
                throw new AgentBrokerException("session_conflict");
            }

            ActiveSessionId = sessionId;
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
            CapabilityProfile profile,
            CancellationToken cancellationToken)
        {
            Calls.Add($"execute:{call.SessionId}");
            if (!string.Equals(ActiveSessionId, call.SessionId, StringComparison.Ordinal))
            {
                throw new AgentBrokerException("broker_not_ready");
            }

            ExecuteCount++;
            LastCall = call;
            ExecutionStarted.TrySetResult();
            if (BlockExecution)
            {
                await ReleaseExecution.Task.ConfigureAwait(false);
            }

            return new AgentToolResult(
                ResultRequestId ?? call.RequestId,
                ResultStatus,
                new JsonObject { ["operation"] = "set", ["level"] = 2 },
                ResultErrorCode,
                ResultSafeSummary,
                12,
                ResultVerificationState,
                ResultEndpointRole,
                ResultEndpointHash,
                AdapterInvoked);
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
            CancellationToken cancellationToken)
        {
            Calls.Add($"close:{sessionId}");
            CloseCount++;
            if (CloseErrorCode is not null)
            {
                throw new AgentBrokerException(CloseErrorCode);
            }

            if (string.Equals(ActiveSessionId, sessionId, StringComparison.Ordinal))
            {
                ActiveSessionId = null;
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingEmitter : IDiagnosticEmitter
    {
        public List<EmittedEvent> Events { get; } = [];

        public bool TryEmit(
            string level,
            string component,
            string eventCode,
            string? correlationId = null,
            double? durationMs = null,
            string? status = null,
            string? errorCode = null,
            string? exceptionKind = null,
            string? toolId = null,
            string? toolVersion = null,
            string? verificationState = null,
            string? endpointRole = null,
            string? endpointHash = null)
        {
            Events.Add(new EmittedEvent(
                eventCode,
                correlationId,
                durationMs,
                status,
                errorCode,
                toolId,
                toolVersion,
                verificationState,
                endpointRole,
                endpointHash));
            return true;
        }
    }

    private sealed record EmittedEvent(
        string EventCode,
        string? CorrelationId,
        double? DurationMs,
        string? Status,
        string? ErrorCode,
        string? ToolId,
        string? ToolVersion,
        string? VerificationState,
        string? EndpointRole,
        string? EndpointHash);
}
