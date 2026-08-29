using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Devices;
using RuAgent.Runtime.Core.Contracts;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class VtubeStudioClientTests
{
    [Fact]
    public async Task Client_authenticates_with_existing_token_and_builds_sorted_catalog()
    {
        var socket = new FakeSocket(
            Response("AuthenticationResponse", new JsonObject { ["authenticated"] = true }),
            Response("CurrentModelResponse", new JsonObject
            {
                ["modelLoaded"] = true,
                ["modelID"] = "model-1",
                ["modelName"] = "Ru",
            }),
            Response("HotkeysInCurrentModelResponse", new JsonObject
            {
                ["availableHotkeys"] = new JsonArray(
                    new JsonObject { ["name"] = "Zoo", ["hotkeyID"] = "hk-z" },
                    new JsonObject { ["name"] = "Alpha", ["hotkeyID"] = "hk-a" }),
            }),
            Response("ExpressionStateResponse", new JsonObject
            {
                ["expressions"] = new JsonArray(
                    new JsonObject { ["name"] = "Smile", ["expressionFile"] = "smile.exp3.json" },
                    new JsonObject { ["name"] = "Ignored", ["expressionFile"] = "ignored.json" }),
            }));
        var client = Client(socket, new MemoryTokenStore("stored-token"));

        var catalog = await client.ReadCatalogAsync(CancellationToken.None);

        Assert.Equal("model-1", catalog.ModelId);
        Assert.Equal("Ru", catalog.ModelName);
        Assert.Equal(["vts_01", "vts_02"], catalog.HotkeyCodes);
        Assert.Equal(["expr_01"], catalog.ExpressionCodes);
        Assert.Equal("AuthenticationRequest", socket.Requests[0]["messageType"]!.GetValue<string>());
        Assert.Equal("stored-token", socket.Requests[0]["data"]!["authenticationToken"]!.GetValue<string>());
        Assert.Equal("HotkeysInCurrentModelRequest", socket.Requests[2]["messageType"]!.GetValue<string>());
    }

    [Fact]
    public async Task Client_requests_new_token_when_existing_authentication_is_rejected()
    {
        var socket = new FakeSocket(
            Response("AuthenticationResponse", new JsonObject { ["authenticated"] = false }),
            Response("AuthenticationTokenResponse", new JsonObject { ["authenticationToken"] = "new-token" }),
            Response("AuthenticationResponse", new JsonObject { ["authenticated"] = true }),
            Response("CurrentModelResponse", new JsonObject
            {
                ["modelLoaded"] = false,
                ["modelID"] = "",
                ["modelName"] = "",
            }));
        var store = new MemoryTokenStore("old-token");
        var client = Client(socket, store);

        var error = await Assert.ThrowsAsync<VtubeStudioProviderException>(() => client.ReadCatalogAsync(
            CancellationToken.None));

        Assert.Equal("new-token", store.Token);
        Assert.Equal("vts_catalog_unavailable", error.Code);
        Assert.Equal("AuthenticationTokenRequest", socket.Requests[1]["messageType"]!.GetValue<string>());
        Assert.Equal("AuthenticationRequest", socket.Requests[2]["messageType"]!.GetValue<string>());
    }

    [Fact]
    public async Task Client_revalidates_catalog_before_each_action_and_rejects_unknown_or_duplicate_codes()
    {
        var socket = new FakeSocket(
            Response("AuthenticationResponse", new JsonObject { ["authenticated"] = true }),
            CatalogResponses("model-1", "Ru", "hk-a"),
            CatalogResponses("model-1", "Ru", "hk-a"),
            Response("HotkeyTriggerResponse", new JsonObject()),
            CatalogResponses("model-2", "Other", string.Empty));
        var client = Client(socket, new MemoryTokenStore("stored-token"));
        _ = await client.ReadCatalogAsync(CancellationToken.None);

        await client.TriggerAsync(["vts_01"], CancellationToken.None);

        Assert.Equal("HotkeyTriggerRequest", socket.Requests[^1]["messageType"]!.GetValue<string>());
        await Assert.ThrowsAsync<VtubeStudioProviderException>(() => client.TriggerAsync(
            ["vts_01", "vts_01"],
            CancellationToken.None));
        VtubeStudioProviderException error;
        try
        {
            await client.TriggerAsync(["vts_01"], CancellationToken.None);
            throw new Xunit.Sdk.XunitException("expected unknown code");
        }
        catch (VtubeStudioProviderException caught)
        {
            error = caught;
        }
        Assert.Equal(
            [
                "AuthenticationRequest",
                "CurrentModelRequest", "HotkeysInCurrentModelRequest", "ExpressionStateRequest",
                "CurrentModelRequest", "HotkeysInCurrentModelRequest", "ExpressionStateRequest", "HotkeyTriggerRequest",
                "CurrentModelRequest", "HotkeysInCurrentModelRequest", "ExpressionStateRequest",
            ],
            socket.Requests.Select(request => request["messageType"]!.GetValue<string>()));
        Assert.Equal("unknown_vts_code", error.Code);
    }

    [Fact]
    public async Task Client_maps_fragmented_malformed_and_oversized_messages_to_bounded_errors()
    {
        var socket = new FakeSocket(
            Response("AuthenticationResponse", new JsonObject { ["authenticated"] = true }),
            new FakeFrame(WebSocketMessageType.Text, false, Encoding.UTF8.GetBytes("{\"messageType\":\"CurrentModelResponse\",")),
            new FakeFrame(WebSocketMessageType.Text, true, Encoding.UTF8.GetBytes("\"data\":{\"modelLoaded\":false}}")));
        var client = Client(socket, new MemoryTokenStore("stored-token"));

        var error = await Assert.ThrowsAsync<VtubeStudioProviderException>(() => client.ReadCatalogAsync(
            CancellationToken.None));

        Assert.Equal("malformed_response", error.Code);
        Assert.DoesNotContain("modelLoaded", error.Message, StringComparison.Ordinal);

        var oversizedSocket = new FakeSocket(
            Response("AuthenticationResponse", new JsonObject { ["authenticated"] = true }),
            new FakeFrame(WebSocketMessageType.Text, true, new byte[600]));
        var boundedClient = Client(oversizedSocket, new MemoryTokenStore("stored-token"), maxMessageBytes: 512);
        var oversized = await Assert.ThrowsAsync<VtubeStudioProviderException>(() => boundedClient.ReadCatalogAsync(
            CancellationToken.None));
        Assert.Equal("response_too_large", oversized.Code);
    }

    [Fact]
    public async Task Client_reconnects_on_the_next_request_after_socket_failure()
    {
        var first = new FakeSocket(
            Response("AuthenticationResponse", new JsonObject { ["authenticated"] = true }));
        var second = new FakeSocket(
            Response("AuthenticationResponse", new JsonObject { ["authenticated"] = true }),
            CatalogResponses("model-2", "Ru", "hk-a"));
        var client = new VtubeStudioClient(
            new VtubeStudioClientOptions(new Uri("ws://127.0.0.1:8001")),
            new MemoryTokenStore("stored-token"),
            new FakeSocketFactory(first, second));

        var firstError = await Assert.ThrowsAsync<VtubeStudioProviderException>(() => client.ReadCatalogAsync(
            CancellationToken.None));
        var catalog = await client.ReadCatalogAsync(CancellationToken.None);

        Assert.Equal("vts_unavailable", firstError.Code);
        Assert.Equal("model-2", catalog.ModelId);
        Assert.Equal(1, second.Requests.Count(request =>
            request["messageType"]!.GetValue<string>() == "AuthenticationRequest"));
    }

    private static VtubeStudioClient Client(
        FakeSocket socket,
        MemoryTokenStore store,
        int maxMessageBytes = 256 * 1024) =>
        new(
            new VtubeStudioClientOptions(new Uri("ws://127.0.0.1:8001"))
            {
                MaxMessageBytes = maxMessageBytes,
            },
            store,
            new FakeSocketFactory(socket));

    private static JsonObject Response(string type, JsonObject data) => new()
    {
        ["apiName"] = "VTubeStudioPublicAPI",
        ["apiVersion"] = "1.0",
        ["requestID"] = "response",
        ["messageType"] = type,
        ["data"] = data,
    };

    private static FakeFrame[] CatalogResponses(string modelId, string modelName, string hotkeyId) =>
        [
            Frame(Response("CurrentModelResponse", new JsonObject
            {
                ["modelLoaded"] = true,
                ["modelID"] = modelId,
                ["modelName"] = modelName,
            })),
            Frame(Response("HotkeysInCurrentModelResponse", new JsonObject
            {
                ["availableHotkeys"] = string.IsNullOrEmpty(hotkeyId)
                    ? new JsonArray()
                    : new JsonArray(
                        new JsonObject { ["name"] = "Alpha", ["hotkeyID"] = hotkeyId }),
            })),
            Frame(Response("ExpressionStateResponse", new JsonObject
            {
                ["expressions"] = new JsonArray(),
            })),
        ];

    private static FakeFrame Frame(JsonObject response) => new(
        WebSocketMessageType.Text,
        true,
        Encoding.UTF8.GetBytes(response.ToJsonString()));

    private sealed class MemoryTokenStore : IVtubeStudioTokenStore
    {
        public MemoryTokenStore(string? token) => Token = token;
        public string? Token { get; private set; }
        public Task<string?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Token);
        public Task WriteAsync(string token, CancellationToken cancellationToken)
        {
            Token = token;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSocketFactory : IVtubeStudioWebSocketFactory
    {
        private readonly Queue<FakeSocket> _sockets;
        public FakeSocketFactory(params FakeSocket[] sockets) => _sockets = new Queue<FakeSocket>(sockets);
        public Task<IVtubeStudioWebSocket> ConnectAsync(Uri endpoint, CancellationToken cancellationToken) =>
            Task.FromResult<IVtubeStudioWebSocket>(_sockets.Dequeue());
    }

    private sealed class FakeSocket : IVtubeStudioWebSocket
    {
        private readonly Queue<FakeFrame> _frames;
        public FakeSocket(params object[] responses)
        {
            _frames = new Queue<FakeFrame>(responses.SelectMany(response => response switch
            {
                JsonObject json => [new FakeFrame(WebSocketMessageType.Text, true, Encoding.UTF8.GetBytes(json.ToJsonString()))],
                FakeFrame frame => [frame],
                IEnumerable<FakeFrame> frames => frames,
                _ => throw new InvalidOperationException(),
            }));
        }

        public List<JsonObject> Requests { get; } = [];

        public Task SendTextAsync(string message, CancellationToken cancellationToken)
        {
            Requests.Add(JsonNode.Parse(message)!.AsObject());
            return Task.CompletedTask;
        }

        public ValueTask<VtubeStudioWebSocketFrame> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (_frames.Count == 0)
            {
                throw new WebSocketException("no fake response");
            }

            var frame = _frames.Dequeue();
            return ValueTask.FromResult(new VtubeStudioWebSocketFrame(
                frame.MessageType,
                frame.EndOfMessage,
                frame.Bytes));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record FakeFrame(WebSocketMessageType MessageType, bool EndOfMessage, byte[] Bytes);
}
