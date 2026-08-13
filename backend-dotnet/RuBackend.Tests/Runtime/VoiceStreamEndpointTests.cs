using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Runtime;

public sealed class VoiceStreamEndpointTests
{
    [Fact]
    public async Task Direct_stream_sends_audio_control_immediately_before_non_empty_binary()
    {
        var protocol = new FakeVoiceProtocolClient(
        [
            Event(WorkerMessageTypes.VoiceControl, 0, new JsonObject
            {
                ["state"] = "control",
                ["type"] = "started",
                ["request_id"] = "voice-1",
                ["sequence"] = 0,
            }),
            Event(WorkerMessageTypes.VoiceControl, 1, new JsonObject
            {
                ["state"] = "audio_ready",
                ["type"] = "audio",
                ["request_id"] = "voice-1",
                ["sequence"] = 0,
                ["sample_rate"] = 24000,
                ["format"] = "wav",
            }, "RIFF-wave"u8.ToArray()),
            Event(WorkerMessageTypes.VoiceDone, 2, new JsonObject
            {
                ["state"] = "done",
                ["type"] = "ended",
                ["request_id"] = "voice-1",
                ["sequence"] = 1,
            }),
        ]);
        using var factory = Factory(protocol);
        using var socket = await ConnectAsync(factory, "?token=test-secret");

        await SendTextAsync(socket, JsonSerializer.Serialize(new
        {
            type = "start",
            request_id = "voice-1",
            text = "synthetic",
            voice_label = "Ru (OmniVoice)",
            speed = 1.0,
        }));

        Assert.Equal("started", JsonDocument.Parse(await ReceiveTextAsync(socket))
            .RootElement.GetProperty("type").GetString());
        var audioControl = JsonDocument.Parse(await ReceiveTextAsync(socket));
        Assert.Equal("audio", audioControl.RootElement.GetProperty("type").GetString());
        Assert.DoesNotContain("state", audioControl.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("RIFF-wave", Encoding.ASCII.GetString(await ReceiveBinaryAsync(socket)));
        Assert.Equal("ended", JsonDocument.Parse(await ReceiveTextAsync(socket))
            .RootElement.GetProperty("type").GetString());
        Assert.Equal("voice-1", Assert.Single(protocol.Requests).RequestId);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"unknown\"}")]
    public async Task Invalid_controls_emit_one_failed_event(string payload)
    {
        var protocol = new FakeVoiceProtocolClient([]);
        using var factory = Factory(protocol);
        using var socket = await ConnectAsync(factory, "?token=test-secret");

        await SendTextAsync(socket, payload);
        using var response = JsonDocument.Parse(await ReceiveTextAsync(socket));

        Assert.Equal("failed", response.RootElement.GetProperty("type").GetString());
        Assert.Equal("invalid_request", response.RootElement.GetProperty("error_code").GetString());
        Assert.Empty(protocol.Requests);
    }

    [Fact]
    public async Task Cancel_and_playback_controls_are_forwarded_privately()
    {
        var protocol = new FakeVoiceProtocolClient([], block: true);
        using var factory = Factory(protocol);
        using var socket = await ConnectAsync(factory, "?token=test-secret");
        await SendTextAsync(socket, "{\"type\":\"start\",\"request_id\":\"voice-1\",\"text\":\"synthetic\",\"voice_label\":\"Ru (OmniVoice)\",\"speed\":1.0}");
        await protocol.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await SendTextAsync(socket, "{\"type\":\"playback\",\"request_id\":\"voice-1\",\"state\":\"started\"}");
        await SendTextAsync(socket, "{\"type\":\"cancel\",\"request_id\":\"voice-1\"}");

        await protocol.SignalsObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(
            [("playback", "started"), ("cancel", (string?)null)],
            protocol.Signals.Select(item => (item.Signal, item.State)));
        protocol.Release.TrySetResult();
    }

    [Theory]
    [InlineData("")]
    [InlineData("?token=wrong")]
    public async Task Missing_or_invalid_token_closes_with_4401(string query)
    {
        using var factory = Factory(new FakeVoiceProtocolClient([]));
        using var socket = await ConnectAsync(factory, query);

        var buffer = new byte[32];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(4401, (int?)result.CloseStatus);
    }

    private static BackendWebApplicationFactory Factory(IVoiceProtocolClient protocol) =>
        new(voiceProtocolClient: protocol);

    private static Task<WebSocket> ConnectAsync(BackendWebApplicationFactory factory, string query) =>
        factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri("ws://localhost/voice/stream" + query),
            CancellationToken.None);

    private static Task SendTextAsync(WebSocket socket, string text) => socket.SendAsync(
        Encoding.UTF8.GetBytes(text),
        WebSocketMessageType.Text,
        true,
        CancellationToken.None);

    private static async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        var (type, bytes) = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Text, type);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]> ReceiveBinaryAsync(WebSocket socket)
    {
        var (type, bytes) = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Binary, type);
        return bytes;
    }

    private static async Task<(WebSocketMessageType, byte[])> ReceiveAsync(WebSocket socket)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return (result.MessageType, stream.ToArray());
    }

    private static WorkerEvent Event(
        string type,
        long sequence,
        JsonObject payload,
        byte[]? binary = null) =>
        new("voice-1", type, sequence, payload, binary ?? []);

    private sealed class FakeVoiceProtocolClient : IVoiceProtocolClient
    {
        private readonly IReadOnlyList<WorkerEvent> _events;
        private readonly bool _block;

        public FakeVoiceProtocolClient(IReadOnlyList<WorkerEvent> events, bool block = false)
        {
            _events = events;
            _block = block;
        }

        public List<WorkerVoiceStreamRequest> Requests { get; } = [];
        public List<WorkerVoiceSignal> Signals { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SignalsObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<WorkerEvent> StreamVoiceAsync(
            WorkerVoiceStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Started.TrySetResult();
            if (_block)
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            foreach (var item in _events)
            {
                yield return item;
            }
        }

        public Task SendVoiceSignalAsync(
            WorkerVoiceSignal signal,
            CancellationToken cancellationToken)
        {
            Signals.Add(signal);
            if (Signals.Count >= 2)
            {
                SignalsObserved.TrySetResult();
            }
            return Task.CompletedTask;
        }
    }
}
