using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using RuBackend.Tests;
using RuAgent.Protocol;
using RuBackend.Agent;
using RuBackend.Conversation;
using RuBackend.Diagnostics;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Conversation;
/// Phase-4 cutover: the Chat WebSocket is read-only Chat. Any `mode` or
/// legacy `agent_mode` request must be rejected fail-closed and never reach
/// the worker as an Agent turn. Agent traffic uses the authenticated
/// /agent/v1/* API.
/// </summary>
public sealed class AgentModeProfileTests
{
    [Fact]
    public async Task Chat_frame_with_mode_agent_is_rejected_with_agent_requires_harness()
    {
        var broker = new FakeBroker();
        var worker = new FakeWorker();
        var diagnostics = new RecordingEmitter();
        using var factory = Factory(worker, broker, diagnostics);
        using var socket = await ConnectAsync(factory);
        await SendTextAsync(
            socket,
            "{\"prompt\":\"Ghi giúp anh\",\"session_id\":\"session-1\",\"mode\":\"agent\"}");

        var frames = await ReceiveUntilErrorAsync(socket);
        Assert.Contains(
            frames,
            frame => frame.RootElement.GetProperty("type").GetString() == "error"
                && frame.RootElement.GetProperty("error_code").GetString() == "agent_requires_harness");
        Assert.Contains(DiagnosticCodes.HarnessChatRejected, diagnostics.Events);
        Assert.Empty(worker.Requests);
        await CloseAsync(socket);
    }

    [Fact]
    public async Task Chat_frame_with_mode_full_is_rejected_with_agent_requires_harness()
    {
        var broker = new FakeBroker();
        var worker = new FakeWorker();
        var diagnostics = new RecordingEmitter();
        using var factory = Factory(worker, broker, diagnostics);
        using var socket = await ConnectAsync(factory);
        await SendTextAsync(
            socket,
            "{\"prompt\":\"Chạy đi\",\"session_id\":\"session-full\",\"mode\":\"full\"}");

        var frames = await ReceiveUntilErrorAsync(socket);
        Assert.Contains(
            frames,
            frame => frame.RootElement.GetProperty("type").GetString() == "error"
                && frame.RootElement.GetProperty("error_code").GetString() == "agent_requires_harness");
        Assert.Empty(worker.Requests);
        await CloseAsync(socket);
    }

    [Fact]
    public async Task Legacy_agent_mode_true_is_rejected_with_agent_requires_harness()
    {
        var broker = new FakeBroker();
        var worker = new FakeWorker();
        var diagnostics = new RecordingEmitter();
        using var factory = Factory(worker, broker, diagnostics);
        using var socket = await ConnectAsync(factory);
        await SendTextAsync(
            socket,
            "{\"prompt\":\"Xin chào\",\"session_id\":\"session-1\",\"agent_mode\":true}");

        var frames = await ReceiveUntilErrorAsync(socket);
        Assert.Contains(
            frames,
            frame => frame.RootElement.GetProperty("type").GetString() == "error"
                && frame.RootElement.GetProperty("error_code").GetString() == "agent_requires_harness");
        Assert.Empty(worker.Requests);
        await CloseAsync(socket);
    }

    [Fact]
    public async Task Agent_approval_response_is_rejected_on_chat_websocket()
    {
        var broker = new FakeBroker();
        var worker = new FakeWorker();
        using var factory = Factory(worker, broker);
        using var socket = await ConnectAsync(factory);
        await SendTextAsync(
            socket,
            "{\"type\":\"agent.approval.response\",\"session_id\":\"session-1\",\"approval_id\":\"a1\",\"approved\":true}");

        var frames = await ReceiveUntilErrorAsync(socket);
        Assert.Contains(
            frames,
            frame => frame.RootElement.GetProperty("type").GetString() == "error"
                && frame.RootElement.GetProperty("error_code").GetString() == "agent_requires_harness");
        await CloseAsync(socket);
    }

    [Fact]
    public async Task Chat_frame_with_explicit_mode_chat_still_completes()
    {
        var broker = new FakeBroker();
        var worker = new FakeWorker();
        using var factory = Factory(worker, broker);
        using var socket = await ConnectAsync(factory);
        await SendTextAsync(
            socket,
            "{\"prompt\":\"Kiểm tra việc này giúp anh\",\"session_id\":\"session-1\",\"mode\":\"chat\"}");
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitUntilAsync(() => worker.Done.Task.IsCompleted, TimeSpan.FromSeconds(5));

        var settings = Assert.Single(worker.Requests).Settings;
        Assert.False(settings["agent_mode"]!.GetValue<bool>());
        var tools = settings["model_tools"]!.AsArray();
        Assert.DoesNotContain(
            tools,
            item => item!["tool_id"]!.GetValue<string>() == "files.write");
        await CloseAsync(socket);
    }

    private static async Task<List<JsonDocument>> ReceiveUntilErrorAsync(WebSocket socket)
    {
        var frames = new List<JsonDocument>();
        while (true)
        {
            var text = await ReceiveTextAsync(socket);
            var document = JsonDocument.Parse(text);
            frames.Add(document);
            if (document.RootElement.GetProperty("type").GetString() == "error")
            {
                return frames;
            }
        }
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

    private static BackendWebApplicationFactory Factory(
        FakeWorker worker,
        FakeBroker broker,
        RecordingEmitter? diagnostics = null) =>
        new(
            workerClient: worker,
            agentBrokerClient: broker,
            diagnosticEmitter: diagnostics ?? new RecordingEmitter());

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
                _ => AgentTools,
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
