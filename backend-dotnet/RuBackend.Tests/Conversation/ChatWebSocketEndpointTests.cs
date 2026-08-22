using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using RuAgent.Protocol;
using RuBackend.Agent;
using RuBackend.Conversation;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Conversation;

public sealed class ChatWebSocketEndpointTests
{
    [Fact]
    public async Task Valid_request_reaches_the_coordinator_and_events_keep_order()
    {
        var coordinator = new FakeCoordinator();
        using var factory = Factory(coordinator);
        using var socket = await ConnectAsync(factory, "?token=test-secret");

        await SendTextAsync(socket, "{\"prompt\":\"Xin chào\",\"session_id\":\"session-1\"}");
        var events = new List<JsonElement>();
        for (var index = 0; index < 4; index++)
        {
            events.Add(JsonDocument.Parse(await ReceiveTextAsync(socket)).RootElement.Clone());
        }

        Assert.Equal(["session", "reset", "chunk", "done"],
            events.Select(item => item.GetProperty("type").GetString()));
        var request = Assert.Single(coordinator.Requests);
        Assert.Equal("Xin chào", request.Prompt);
        Assert.Equal("session-1", request.SessionId);
        Assert.DoesNotContain("error_code", events[^1].GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sequential_requests_after_done_are_not_consumed_by_disconnect_monitor()
    {
        var coordinator = new FakeCoordinator();
        using var factory = Factory(coordinator);
        using var socket = await ConnectAsync(factory, "?token=test-secret");

        await SendTextAsync(socket, "{\"prompt\":\"Lượt một\"}");
        for (var index = 0; index < 4; index++)
        {
            await ReceiveTextAsync(socket);
        }

        await SendTextAsync(socket, "{\"prompt\":\"Lượt hai\"}");
        var secondTurn = new List<JsonElement>();
        for (var index = 0; index < 4; index++)
        {
            secondTurn.Add(JsonDocument.Parse(await ReceiveTextAsync(socket)).RootElement.Clone());
        }

        Assert.Equal(["session", "reset", "chunk", "done"],
            secondTurn.Select(item => item.GetProperty("type").GetString()));
        Assert.Equal(["Lượt một", "Lượt hai"], coordinator.Requests.Select(request => request.Prompt));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"prompt\":123}")]
    public async Task Malformed_frames_emit_one_controlled_error(string payload)
    {
        var coordinator = new FakeCoordinator();
        using var factory = Factory(coordinator);
        using var socket = await ConnectAsync(factory, "?token=test-secret");

        await SendTextAsync(socket, payload);
        using var response = JsonDocument.Parse(await ReceiveTextAsync(socket));

        Assert.Equal("error", response.RootElement.GetProperty("type").GetString());
        Assert.Equal("Malformed message.", response.RootElement.GetProperty("content").GetString());
        Assert.Empty(coordinator.Requests);
    }

    [Fact]
    public async Task Oversized_message_is_rejected_before_JSON_or_coordinator_processing()
    {
        var coordinator = new FakeCoordinator();
        using var factory = Factory(coordinator);
        using var socket = await ConnectAsync(factory, "?token=test-secret");

        await SendTextAsync(socket, new string('x', ChatRequest.MaxMessageBytes + 1));
        using var response = JsonDocument.Parse(await ReceiveTextAsync(socket));

        Assert.Equal("message_too_large", response.RootElement.GetProperty("error_code").GetString());
        Assert.Equal("Message too large.", response.RootElement.GetProperty("content").GetString());
        Assert.Empty(coordinator.Requests);
    }

    [Fact]
    public async Task Blank_prompt_is_ignored_and_the_next_valid_request_is_processed_once()
    {
        var coordinator = new FakeCoordinator();
        using var factory = Factory(coordinator);
        using var socket = await ConnectAsync(factory, "?token=test-secret");

        await SendTextAsync(socket, "{\"prompt\":\"   \"}");
        await SendTextAsync(socket, "{\"prompt\":\"Tiếp tục\"}");
        for (var index = 0; index < 4; index++)
        {
            await ReceiveTextAsync(socket);
        }

        Assert.Equal("Tiếp tục", Assert.Single(coordinator.Requests).Prompt);
    }

    [Fact]
    public async Task Prompt_over_character_limit_is_rejected_without_calling_coordinator()
    {
        var coordinator = new FakeCoordinator();
        using var factory = Factory(coordinator);
        using var socket = await ConnectAsync(factory, "?token=test-secret");
        var payload = JsonSerializer.Serialize(new { prompt = new string('x', 16_001) });

        await SendTextAsync(socket, payload);
        using var response = JsonDocument.Parse(await ReceiveTextAsync(socket));

        Assert.Equal("prompt_too_long", response.RootElement.GetProperty("error_code").GetString());
        Assert.Equal("Prompt too long (max 16000 characters).", response.RootElement.GetProperty("content").GetString());
        Assert.Empty(coordinator.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?token=wrong")]
    public async Task Missing_or_invalid_token_closes_with_4401(string query)
    {
        using var factory = Factory(new FakeCoordinator());
        using var socket = await ConnectAsync(factory, query);

        var buffer = new byte[32];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(4401, (int?)result.CloseStatus);
    }

    [Fact]
    public async Task Client_disconnect_cancels_the_active_coordinator_turn()
    {
        var coordinator = new FakeCoordinator { Block = true };
        using var factory = Factory(coordinator);
        using var socket = await ConnectAsync(factory, "?token=test-secret");
        await SendTextAsync(socket, "{\"prompt\":\"Xin chào\"}");
        await coordinator.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await socket.CloseOutputAsync(
            WebSocketCloseStatus.NormalClosure,
            "test disconnect",
            CancellationToken.None);

        await coordinator.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Approval_control_message_reaches_the_approval_coordinator()
    {
        var coordinator = new FakeCoordinator();
        var approvals = new FakeApprovalCoordinator();
        using var factory = Factory(coordinator, approvals);
        using var socket = await ConnectAsync(factory, "?token=test-secret");

        await SendTextAsync(
            socket,
            "{\"type\":\"agent.approval.response\",\"session_id\":\"session-1\",\"approval_id\":\"approval-1\",\"approved\":false}");
        using var response = JsonDocument.Parse(await ReceiveTextAsync(socket));

        Assert.Equal("agent.approval_result", response.RootElement.GetProperty("type").GetString());
        Assert.Equal("approval-1", response.RootElement.GetProperty("approval_id").GetString());
        Assert.Equal("denied", response.RootElement.GetProperty("status").GetString());
        Assert.Equal("approval-1", approvals.LastResponse!.ApprovalId);
        Assert.False(approvals.LastResponse.Approved);
    }

    private static BackendWebApplicationFactory Factory(
        IConversationCoordinator coordinator,
        IAgentApprovalCoordinator? approvals = null) =>
        new(conversationCoordinator: coordinator, approvalCoordinator: approvals);

    private static Task<WebSocket> ConnectAsync(
        BackendWebApplicationFactory factory,
        string query)
    {
        var client = factory.Server.CreateWebSocketClient();
        return client.ConnectAsync(new Uri("ws://localhost/chat" + query), CancellationToken.None);
    }

    private static Task SendTextAsync(WebSocket socket, string value) =>
        socket.SendAsync(
            Encoding.UTF8.GetBytes(value),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

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

    private sealed class FakeCoordinator : IConversationCoordinator
    {
        public bool Block { get; init; }
        public List<ChatRequest> Requests { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ChatEvent> HandleAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Started.TrySetResult();
            if (Block)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Cancelled.TrySetResult();
                    }
                }
            }

            yield return new ChatEvent("session", SessionId: "session-created");
            yield return new ChatEvent("reset");
            yield return new ChatEvent("chunk", "Em nghe đây.");
            yield return new ChatEvent("done", "Em nghe đây.", "session-created");
        }
    }

    private sealed class FakeApprovalCoordinator : IAgentApprovalCoordinator
    {
        public AgentApprovalResponse? LastResponse { get; private set; }

        public ChatEvent Register(WorkerToolRequest tool, AgentToolResult result) =>
            new("agent.approval_required", "Approval required.", tool.SessionId);

        public Task<AgentToolResult> RespondAsync(
            AgentApprovalResponse response,
            CancellationToken cancellationToken)
        {
            LastResponse = response;
            return Task.FromResult(new AgentToolResult(
                response.ApprovalId,
                response.Approved ? ToolResultStatuses.Succeeded : ToolResultStatuses.Denied,
                new System.Text.Json.Nodes.JsonObject(),
                response.Approved ? null : "approval_rejected",
                response.Approved ? "Approved action completed." : "Approval was rejected."));
        }
    }
}
