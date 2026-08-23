using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using RuAgent.Protocol;
using RuBackend.Agent;
using RuBackend.Conversation;
using RuBackend.Diagnostics;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Conversation;

public sealed class AgentModeProfileTests
{
    [Theory]
    [InlineData("chat", false, CapabilityProfile.Chat, false)]
    [InlineData("agent", false, CapabilityProfile.Agent, false)]
    [InlineData("full", false, CapabilityProfile.Full, false)]
    [InlineData(null, false, CapabilityProfile.Chat, false)]
    [InlineData(null, true, CapabilityProfile.Agent, false)]
    [InlineData("yolo", true, CapabilityProfile.Chat, true)]
    [InlineData("AGENT", false, CapabilityProfile.Chat, true)]
    public void ProfileForTurn_maps_modes_legacy_flag_and_unknown_clamps(
        string? mode,
        bool legacyAgentMode,
        CapabilityProfile expectedProfile,
        bool expectedClamp)
    {
        var (profile, clamped) = AgentTurnCoordinator.ProfileForTurn(mode, legacyAgentMode);

        Assert.Equal(expectedProfile, profile);
        Assert.Equal(expectedClamp, clamped);
    }

    [Fact]
    public async Task Agent_frame_advertises_agent_tools_and_flags_verification_only_for_volume()
    {
        var broker = new FakeBroker();
        var worker = new FakeWorker();
        using var factory = Factory(worker, broker);
        using var socket = await ConnectAsync(factory);
        await SendTextAsync(
            socket,
            "{\"prompt\":\"Xin chào\",\"session_id\":\"session-1\",\"mode\":\"agent\"}");
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitUntilAsync(() => worker.Done.Task.IsCompleted, TimeSpan.FromSeconds(5));

        var settings = Assert.Single(worker.Requests).Settings;
        Assert.True(settings["agent_mode"]!.GetValue<bool>());
        var tools = settings["model_tools"]!.AsArray();
        var ids = tools.Select(item => item!["tool_id"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("files.write", ids);
        Assert.Contains("system.volume", ids);
        foreach (var entry in tools)
        {
            var toolId = entry!["tool_id"]!.GetValue<string>();
            Assert.Equal(
                string.Equals(toolId, "system.volume", StringComparison.Ordinal),
                entry["verification_required"]!.GetValue<bool>());
            Assert.Equal(
                string.Equals(toolId, "system.volume", StringComparison.Ordinal)
                    ? "native_mutation"
                    : "tool",
                entry["capability_class"]!.GetValue<string>());
        }

        Assert.Equal(
            """{"path":"ten-file-trong-workspace"}""",
            tools.Single(item => item!["tool_id"]!.GetValue<string>() == "files.read")!["hint"]!
                .GetValue<string>());
        Assert.Equal(
            """{"operation":"set","level":30}""",
            tools.Single(item => item!["tool_id"]!.GetValue<string>() == "system.volume")!["hint"]!
                .GetValue<string>());

        await CloseAsync(socket);
    }

    [Fact]
    public async Task Full_frame_uses_the_broker_terminal_run_argument_contract()
    {
        var broker = new FakeBroker();
        var worker = new FakeWorker();
        using var factory = Factory(worker, broker);
        using var socket = await ConnectAsync(factory);
        await SendTextAsync(
            socket,
            "{\"prompt\":\"Kiểm tra\",\"session_id\":\"session-full\",\"mode\":\"full\"}");
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitUntilAsync(() => worker.Done.Task.IsCompleted, TimeSpan.FromSeconds(5));

        var tools = Assert.Single(worker.Requests).Settings["model_tools"]!.AsArray();
        var terminal = tools.Single(
            item => item!["tool_id"]!.GetValue<string>() == "terminal.run")!;
        Assert.Equal(
            """{"executable":"git","arguments":["status"],"working_directory":".","network":false}""",
            terminal["hint"]!.GetValue<string>());

        await CloseAsync(socket);
    }

    [Fact]
    public async Task Chat_frame_never_advertises_write_tools()
    {
        var broker = new FakeBroker();
        var worker = new FakeWorker();
        using var factory = Factory(worker, broker);
        using var socket = await ConnectAsync(factory);
        await SendTextAsync(
            socket,
            "{\"prompt\":\"Xin chào\",\"session_id\":\"session-1\",\"mode\":\"chat\"}");
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitUntilAsync(() => worker.Done.Task.IsCompleted, TimeSpan.FromSeconds(5));

        var settings = Assert.Single(worker.Requests).Settings;
        Assert.False(settings["agent_mode"]!.GetValue<bool>());
        var tools = settings["model_tools"]!.AsArray();
        Assert.NotEmpty(tools);
        Assert.DoesNotContain(
            tools,
            item => item!["tool_id"]!.GetValue<string>() == "files.write");
        await CloseAsync(socket);
    }

    [Fact]
    public async Task Legacy_agent_mode_true_maps_to_the_agent_set()
    {
        var broker = new FakeBroker();
        var worker = new FakeWorker();
        using var factory = Factory(worker, broker);
        using var socket = await ConnectAsync(factory);
        await SendTextAsync(
            socket,
            "{\"prompt\":\"Xin chào\",\"session_id\":\"session-1\",\"agent_mode\":true}");
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitUntilAsync(() => worker.Done.Task.IsCompleted, TimeSpan.FromSeconds(5));

        var tools = Assert.Single(worker.Requests).Settings["model_tools"]!.AsArray();
        Assert.Contains(
            tools,
            item => item!["tool_id"]!.GetValue<string>() == "files.write");
        await CloseAsync(socket);
    }

    [Fact]
    public async Task Unknown_mode_clamps_to_chat_and_emits_one_bounded_diagnostic()
    {
        var broker = new FakeBroker();
        var worker = new FakeWorker();
        var diagnostics = new RecordingEmitter();
        using var factory = Factory(worker, broker, diagnostics);
        using var socket = await ConnectAsync(factory);
        await SendTextAsync(
            socket,
            "{\"prompt\":\"Xin chào\",\"session_id\":\"session-1\",\"mode\":\"warp\"}");
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitUntilAsync(() => worker.Done.Task.IsCompleted, TimeSpan.FromSeconds(5));

        var tools = Assert.Single(worker.Requests).Settings["model_tools"]!.AsArray();
        Assert.DoesNotContain(
            tools,
            item => item!["tool_id"]!.GetValue<string>() == "files.write");
        var clamp = Assert.Single(
            diagnostics.Events,
            item => item == DiagnosticCodes.ModeClamped);
        Assert.Equal(DiagnosticCodes.ModeClamped, clamp);
        await CloseAsync(socket);
    }

    [Fact]
    public async Task One_profile_scoped_session_serves_every_call_in_a_turn_loop()
    {
        var broker = new FakeBroker();
        var worker = new FakeWorker();
        worker.ToolScripts.Enqueue(ToolRequest("tool-1", "session-1", "files.write", "tool"));
        worker.ToolScripts.Enqueue(ToolRequest("tool-2", "session-1", "system.volume", "native_mutation"));
        using var factory = Factory(worker, broker);
        using var socket = await ConnectAsync(factory);
        await SendTextAsync(
            socket,
            "{\"prompt\":\"Ghi giúp anh\",\"session_id\":\"session-1\",\"mode\":\"agent\"}");
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitUntilAsync(() => worker.Done.Task.IsCompleted, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => broker.ExecuteCount >= 2, TimeSpan.FromSeconds(5));

        Assert.Equal(1, broker.OpenCount);
        Assert.Equal(0, broker.CloseCount);
        Assert.Equal(2, broker.ExecuteCount);
        Assert.Equal(
            new HashSet<string>(
                ["workspace.read", "workspace.write", "system.write"],
                StringComparer.Ordinal),
            broker.LastOpenScopes);
        await CloseAsync(socket);
    }

    [Fact]
    public async Task Session_switch_closes_the_previous_profile_session()
    {
        var broker = new FakeBroker();
        var worker = new FakeWorker();
        worker.ToolScripts.Enqueue(ToolRequest("tool-1", "session-1", "files.write", "tool"));
        using var factory = Factory(worker, broker);
        using var socket = await ConnectAsync(factory);
        await SendTextAsync(
            socket,
            "{\"prompt\":\"Một\",\"session_id\":\"session-1\",\"mode\":\"agent\"}");
        await DrainUntilDoneAsync(socket);

        worker.ToolScripts.Enqueue(ToolRequest("tool-2", "session-2", "system.volume", "native_mutation"));
        await SendTextAsync(
            socket,
            "{\"prompt\":\"Hai\",\"session_id\":\"session-2\",\"mode\":\"agent\"}");
        await WaitUntilAsync(() => worker.Requests.Count == 2, TimeSpan.FromSeconds(5));
        await DrainUntilDoneAsync(socket);

        Assert.Equal(2, broker.OpenCount);
        Assert.Equal(1, broker.CloseCount);
        Assert.Equal(
            [
                "open:session-1", "execute:session-1",
                "close:session-1",
                "open:session-2", "execute:session-2",
            ],
            broker.Calls);
        await CloseAsync(socket);
    }

    [Fact]
    public async Task Cancelling_a_turn_releases_the_profile_session()
    {
        var broker = new FakeBroker { BlockExecution = true };
        var worker = new FakeWorker();
        worker.ToolScripts.Enqueue(ToolRequest("tool-1", "session-1", "files.write", "tool"));
        using var factory = Factory(worker, broker);
        using var socket = await ConnectAsync(factory);
        await SendTextAsync(
            socket,
            "{\"prompt\":\"Chạy đi\",\"session_id\":\"session-1\",\"mode\":\"agent\"}");
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => broker.BlockedReached, TimeSpan.FromSeconds(5));

        await socket.CloseOutputAsync(
            WebSocketCloseStatus.NormalClosure,
            "test disconnect",
            CancellationToken.None);

        await WaitUntilAsync(() => broker.CloseCount == 1, TimeSpan.FromSeconds(5));
        Assert.Null(broker.ActiveSessionId);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task DrainUntilDoneAsync(WebSocket socket)
    {
        while (true)
        {
            using var document = JsonDocument.Parse(await ReceiveTextAsync(socket));
            if (document.RootElement.GetProperty("type").GetString() == "done")
            {
                return;
            }
        }
    }

    private static BackendWebApplicationFactory Factory(
        FakeWorker worker,
        FakeBroker broker,
        RecordingEmitter? diagnostics = null) =>
        new(
            workerClient: worker,
            agentBrokerClient: broker,
            diagnosticEmitter: diagnostics ?? new RecordingEmitter());

    private static WorkerToolRequest ToolRequest(
        string requestId,
        string sessionId,
        string toolId,
        string capabilityClass) => new(
            requestId,
            sessionId,
            $"corr-{requestId}",
            toolId,
            "1.0.0",
            capabilityClass,
            new JsonObject(),
            30_000);

    private static Task<WebSocket> ConnectAsync(BackendWebApplicationFactory factory)
    {
        var client = factory.Server.CreateWebSocketClient();
        return client.ConnectAsync(new Uri("ws://localhost/chat?token=test-secret"), CancellationToken.None);
    }

    private static Task SendTextAsync(WebSocket socket, string value) =>
        socket.SendAsync(
            Encoding.UTF8.GetBytes(value),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

    private static async Task CloseAsync(WebSocket socket)
    {
        try
        {
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "test done",
                CancellationToken.None);
        }
        catch (WebSocketException)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not met before the deadline.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeBroker : IAgentBrokerClient
    {
        private static readonly IReadOnlyList<AgentToolCapability> ChatTools =
        [
            Capability("files.list", "workspace.read"),
            Capability("files.read", "workspace.read"),
        ];

        private static readonly IReadOnlyList<AgentToolCapability> AgentTools =
        [
            Capability("files.list", "workspace.read"),
            Capability("files.read", "workspace.read"),
            Capability("files.write", "workspace.write"),
            Capability("system.volume", "system.write"),
        ];

        private static readonly IReadOnlyList<AgentToolCapability> FullTools =
        [
            .. AgentTools,
            Capability("terminal.run", "terminal.execute"),
            Capability("terminal.shell", "terminal.execute"),
        ];

        public bool BlockExecution { get; init; }
        public bool BlockedReached { get; private set; }
        public string? ActiveSessionId { get; private set; }
        public int OpenCount { get; private set; }
        public int CloseCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public IReadOnlySet<string> LastOpenScopes { get; private set; } =
            new HashSet<string>(StringComparer.Ordinal);
        public List<string> Calls { get; } = [];
        public TaskCompletionSource Blocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<AgentToolCapability>> GetCapabilitiesAsync(
            CapabilityProfile profile,
            CancellationToken cancellationToken) => Task.FromResult(
            profile switch
            {
                CapabilityProfile.Chat => ChatTools,
                CapabilityProfile.Agent => AgentTools,
                _ => FullTools,
            });

        public Task<AgentSessionGrant> OpenSessionAsync(
            string sessionId,
            IReadOnlySet<string> scopes,
            IReadOnlySet<string> skillIds,
            string? correlationId,
            int deadlineMs,
            CancellationToken cancellationToken)
        {
            Calls.Add($"open:{sessionId}");
            ActiveSessionId = sessionId;
            OpenCount++;
            LastOpenScopes = scopes;
            return Task.FromResult(new AgentSessionGrant(
                sessionId,
                scopes,
                skillIds,
                DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public async Task<AgentToolResult> ExecuteToolAsync(
            AgentToolCall call,
            CapabilityProfile profile,
            CancellationToken cancellationToken)
        {
            Calls.Add($"execute:{call.SessionId}");
            ExecuteCount++;
            if (BlockExecution)
            {
                BlockedReached = true;
                Blocked.TrySetResult();
                await ReleaseGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new AgentToolResult(
                call.RequestId,
                ToolResultStatuses.Succeeded,
                new JsonObject(),
                null,
                "Tool completed.",
                5,
                ToolVerificationStates.Verified,
                null,
                null,
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
            CancellationToken cancellationToken)
        {
            Calls.Add($"close:{sessionId}");
            CloseCount++;
            if (string.Equals(ActiveSessionId, sessionId, StringComparison.Ordinal))
            {
                ActiveSessionId = null;
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static AgentToolCapability Capability(string toolId, string scope) => new(
            toolId,
            "1.0.0",
            new HashSet<string>([scope], StringComparer.Ordinal),
            "read",
            "executable",
            true);
    }

    private sealed class FakeWorker : IWorkerClient
    {
        public Queue<WorkerToolRequest> ToolScripts { get; } = new();
        public List<WorkerChatRequest> Requests { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<WorkerHello> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WorkerHello(1, new HashSet<string> { "chat" }));

        public async IAsyncEnumerable<WorkerEvent> GenerateChatAsync(
            WorkerChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Started.TrySetResult();
            long sequence = 0;
            while (ToolScripts.TryDequeue(out var tool))
            {
                if (request.ToolHandler is null)
                {
                    throw new InvalidOperationException("Tool handler was not bound.");
                }

                _ = await request.ToolHandler(tool, cancellationToken).ConfigureAwait(false);
                yield return Event(request.RequestId, WorkerMessageTypes.ChatToolRequest, sequence++, new JsonObject
                {
                    ["tool_id"] = tool.ToolId,
                });
            }

            Done.TrySetResult();
            yield return Event(
                request.RequestId,
                WorkerMessageTypes.ChatDone,
                sequence++,
                new JsonObject { ["raw_reply"] = "Em nghe đây." });
        }

        public Task<VtsCatalog?> GetVtsCatalogAsync(string requestId, CancellationToken cancellationToken) =>
            Task.FromResult<VtsCatalog?>(null);

        public Task TriggerVtsActionsAsync(
            string requestId,
            IReadOnlyList<string> codes,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<WorkerFact>> ExtractFactsAsync(
            string requestId,
            string prompt,
            string response,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkerFact>>([]);

        public Task CancelAsync(string requestId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<WorkerHealth> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WorkerHealth(true, 1, new HashSet<string>()));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static WorkerEvent Event(string requestId, string type, long sequence, JsonObject payload) =>
            new("worker-request", type, sequence, payload, ReadOnlyMemory<byte>.Empty);
    }

    private sealed class RecordingEmitter : IDiagnosticEmitter
    {
        public List<string> Events { get; } = [];

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
            Events.Add(eventCode);
            return true;
        }
    }
}
