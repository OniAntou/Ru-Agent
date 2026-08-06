using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RuApp.Services;

public sealed record VoiceAudioChunk(
    string RequestId,
    int Sequence,
    byte[] Audio,
    int SampleRate,
    string Format);

public sealed record VoiceStreamTerminalEvent(
    string Type,
    string RequestId,
    string? ErrorCode);

public sealed class VoiceStreamClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri _endpoint;
    private readonly object _gate = new();
    private ClientWebSocket? _socket;
    private bool _disposed;

    public VoiceStreamClient(string authSecret, int port = 8000)
    {
        _endpoint = new Uri(
            $"ws://127.0.0.1:{port}/voice/stream?token={Uri.EscapeDataString(authSecret)}");
    }

    public static string SerializeStartFrame(string requestId, string text, string voiceLabel, double speed)
    {
        return JsonSerializer.Serialize(
            new { type = "start", request_id = requestId, text, voice_label = voiceLabel, speed },
            JsonOptions);
    }

    public static string SerializeCancelFrame(string requestId)
    {
        return JsonSerializer.Serialize(new { type = "cancel", request_id = requestId }, JsonOptions);
    }

    public async Task StartAsync(
        string requestId,
        string text,
        string voiceLabel,
        double speed,
        Func<VoiceAudioChunk, Task> onChunk,
        Func<VoiceStreamTerminalEvent, Task> onTerminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        ArgumentNullException.ThrowIfNull(onChunk);
        ArgumentNullException.ThrowIfNull(onTerminal);
        ThrowIfDisposed();

        var socket = new ClientWebSocket();
        lock (_gate)
        {
            if (_socket is not null)
            {
                socket.Dispose();
                throw new InvalidOperationException("A voice stream is already active.");
            }
            _socket = socket;
        }

        try
        {
            await socket.ConnectAsync(_endpoint, cancellationToken);
            await SendTextAsync(
                socket,
                SerializeStartFrame(requestId, text, voiceLabel, speed),
                cancellationToken);

            VoiceAudioControl? pendingAudio = null;
            while (socket.State == WebSocketState.Open)
            {
                ReceivedMessage? message = await ReceiveMessageAsync(socket, cancellationToken);
                if (message is null)
                {
                    return;
                }

                if (message.MessageType == WebSocketMessageType.Text)
                {
                    using JsonDocument document = JsonDocument.Parse(message.Payload);
                    JsonElement root = document.RootElement;
                    string type = root.GetProperty("type").GetString() ?? string.Empty;
                    string messageRequestId = root.GetProperty("request_id").GetString() ?? string.Empty;
                    if (type == "audio")
                    {
                        pendingAudio = VoiceAudioControl.Parse(root);
                        continue;
                    }

                    if (type is "ended" or "cancelled" or "failed")
                    {
                        await onTerminal(new VoiceStreamTerminalEvent(
                            type,
                            messageRequestId,
                            root.TryGetProperty("error_code", out JsonElement error)
                                ? error.GetString()
                                : null));
                        return;
                    }
                    continue;
                }

                if (message.MessageType != WebSocketMessageType.Binary || pendingAudio is null)
                {
                    throw new InvalidDataException("Voice stream sent an audio frame without metadata.");
                }

                VoiceAudioControl audio = pendingAudio;
                pendingAudio = null;
                await onChunk(new VoiceAudioChunk(
                    audio.RequestId,
                    audio.Sequence,
                    message.Payload,
                    audio.SampleRate,
                    audio.Format));
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_socket, socket))
                {
                    _socket = null;
                }
            }
            socket.Dispose();
        }
    }

    public async Task CancelAsync(string requestId, CancellationToken cancellationToken = default)
    {
        ClientWebSocket? socket;
        lock (_gate)
        {
            socket = _socket;
        }
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }
        await SendTextAsync(socket, SerializeCancelFrame(requestId), cancellationToken);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _socket?.Abort();
            _socket?.Dispose();
            _socket = null;
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private static async Task SendTextAsync(
        ClientWebSocket socket,
        string text,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private static async Task<ReceivedMessage?> ReceiveMessageAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        using var payload = new MemoryStream();
        WebSocketMessageType? messageType = null;
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            messageType ??= result.MessageType;
            if (messageType != result.MessageType)
            {
                throw new InvalidDataException("Voice stream changed message type mid-frame.");
            }
            payload.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return new ReceivedMessage(messageType.Value, payload.ToArray());
            }
        }
    }

    private sealed record ReceivedMessage(WebSocketMessageType MessageType, byte[] Payload);

    private sealed record VoiceAudioControl(
        string RequestId,
        int Sequence,
        int SampleRate,
        string Format)
    {
        public static VoiceAudioControl Parse(JsonElement root)
        {
            return new VoiceAudioControl(
                root.GetProperty("request_id").GetString() ?? throw new InvalidDataException("Missing request ID."),
                root.GetProperty("sequence").GetInt32(),
                root.GetProperty("sample_rate").GetInt32(),
                root.GetProperty("format").GetString() ?? throw new InvalidDataException("Missing audio format."));
        }
    }
}
