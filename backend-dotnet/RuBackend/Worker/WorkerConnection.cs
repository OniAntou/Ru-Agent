using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace RuBackend.Worker;

public sealed class WorkerConnection : IAsyncDisposable
{
    private static readonly IReadOnlySet<string> ResponseTypes = new HashSet<string>(
        [
            WorkerMessageTypes.RuntimeState,
            WorkerMessageTypes.RuntimeResponse,
            WorkerMessageTypes.VoiceControl,
            WorkerMessageTypes.VoiceDone,
            WorkerMessageTypes.VtsCatalog,
            WorkerMessageTypes.VtsAction,
            WorkerMessageTypes.WorkerHealthOk,
            WorkerMessageTypes.Error,
        ],
        StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> TerminalTypes = new HashSet<string>(
        [
            WorkerMessageTypes.RuntimeState,
            WorkerMessageTypes.RuntimeResponse,
            WorkerMessageTypes.VoiceDone,
            WorkerMessageTypes.VtsCatalog,
            WorkerMessageTypes.VtsAction,
            WorkerMessageTypes.WorkerHealthOk,
            WorkerMessageTypes.Error,
        ],
        StringComparer.Ordinal);

    private readonly DeviceWorkerClientOptions _options;
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _connected;
    private bool _disposed;

    public WorkerConnection(DeviceWorkerClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    public async Task<WorkerHello> ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connected)
        {
            throw new InvalidOperationException("Worker connection is already connected.");
        }

        try
        {
            await _socket.ConnectAsync(_options.Uri, cancellationToken);
            var requestId = $"hello-{Guid.NewGuid():N}";
            var request = new WorkerEnvelope(
                DeviceWorkerClientOptions.ProtocolVersion,
                requestId,
                WorkerMessageTypes.Hello,
                null,
                new JsonObject
                {
                    ["token"] = _options.Token,
                    ["version"] = DeviceWorkerClientOptions.ProtocolVersion,
                    ["capabilities"] = new JsonArray(
                        "runtime",
                        "voice",
                        "vts",
                        "observation"),
                });
            await SendEnvelopeCoreAsync(request, cancellationToken);

            var frame = await ReceiveFrameAsync(cancellationToken);
            if (frame.Type != WebSocketMessageType.Text)
            {
                throw new WorkerProtocolException("hello_text_required");
            }

            var response = WorkerEnvelopeCodec.Parse(Encoding.UTF8.GetString(frame.Bytes));
            if (response.Id != requestId
                || response.Type != WorkerMessageTypes.HelloOk
                || response.Seq != 0)
            {
                throw new WorkerProtocolException("invalid_hello");
            }

            var version = ReadInt32(response.Payload["version"], "invalid_hello");
            if (version != DeviceWorkerClientOptions.ProtocolVersion)
            {
                throw new WorkerProtocolException("unsupported_version");
            }

            var capabilities = ReadStringSet(response.Payload["capabilities"], "invalid_hello");
            _connected = true;
            return new WorkerHello(version, capabilities);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (WorkerUnavailableException)
        {
            throw;
        }
        catch (Exception error) when (IsTransportFailure(error))
        {
            throw new WorkerUnavailableException(error);
        }
    }

    public async IAsyncEnumerable<WorkerEvent> SendRequestAsync(
        WorkerEnvelope request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureConnected();
        if (request.Type == WorkerMessageTypes.Hello
            || request.Type == WorkerMessageTypes.HelloOk)
        {
            throw new WorkerProtocolException("unexpected_type");
        }

        await SendEnvelopeAsync(request, cancellationToken);
        long expectedSequence = 0;

        while (true)
        {
            var frame = await ReceiveFrameAsync(cancellationToken);
            if (frame.Type != WebSocketMessageType.Text)
            {
                throw new WorkerProtocolException("unexpected_binary");
            }

            var response = WorkerEnvelopeCodec.Parse(Encoding.UTF8.GetString(frame.Bytes));
            if (!string.Equals(response.Id, request.Id, StringComparison.Ordinal))
            {
                throw new WorkerProtocolException("mismatched_request");
            }

            if (!ResponseTypes.Contains(response.Type))
            {
                throw new WorkerProtocolException("unexpected_type");
            }

            if (response.Seq != expectedSequence)
            {
                throw new WorkerProtocolException("invalid_sequence");
            }

            ReadOnlyMemory<byte> binary = ReadOnlyMemory<byte>.Empty;
            if (AnnouncesBinary(response))
            {
                InboundFrame audioFrame;
                try
                {
                    audioFrame = await ReceiveFrameAsync(cancellationToken);
                }
                catch (WorkerUnavailableException)
                {
                    throw new WorkerProtocolException("missing_binary");
                }

                if (audioFrame.Type != WebSocketMessageType.Binary || audioFrame.Bytes.Length == 0)
                {
                    throw new WorkerProtocolException("missing_binary");
                }

                var expectedBytes = ReadInt64(response.Payload["byte_length"], "invalid_binary_length");
                if (expectedBytes != audioFrame.Bytes.LongLength)
                {
                    throw new WorkerProtocolException("invalid_binary_length");
                }

                binary = audioFrame.Bytes;
            }

            var item = new WorkerEvent(
                response.Id,
                response.Type,
                response.Seq,
                response.Payload.DeepClone().AsObject(),
                binary);
            expectedSequence++;
            yield return item;

            if (TerminalTypes.Contains(response.Type))
            {
                yield break;
            }
        }
    }

    public Task SendEnvelopeAsync(
        WorkerEnvelope envelope,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        return SendEnvelopeCoreAsync(envelope, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _connected = false;
        _socket.Abort();
        _socket.Dispose();
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task SendEnvelopeCoreAsync(
        WorkerEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(WorkerEnvelopeCodec.Serialize(envelope));
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (IsTransportFailure(error))
        {
            throw new WorkerUnavailableException(error);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<InboundFrame> ReceiveFrameAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new MemoryStream();
            var buffer = new byte[16 * 1024];
            WebSocketMessageType? messageType = null;
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WorkerUnavailableException();
                }

                messageType ??= result.MessageType;
                if (messageType != result.MessageType)
                {
                    throw new WorkerProtocolException("invalid_frame");
                }

                if (stream.Length + result.Count > _options.MaxMessageBytes)
                {
                    throw new WorkerProtocolException("message_too_large");
                }

                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            return new InboundFrame(messageType!.Value, stream.ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (WorkerUnavailableException)
        {
            throw;
        }
        catch (Exception error) when (IsTransportFailure(error))
        {
            throw new WorkerUnavailableException(error);
        }
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connected || _socket.State != WebSocketState.Open)
        {
            throw new WorkerUnavailableException();
        }
    }

    private static bool AnnouncesBinary(WorkerEnvelope response)
    {
        if (response.Type == WorkerMessageTypes.VoiceControl
            && response.Payload["state"] is JsonValue state
            && state.TryGetValue<string>(out var value)
            && string.Equals(value, "audio_ready", StringComparison.Ordinal))
        {
            return true;
        }

        return response.Type == WorkerMessageTypes.RuntimeResponse
            && response.Payload["binary"] is JsonValue binary
            && binary.TryGetValue<bool>(out var enabled)
            && enabled;
    }

    private static int ReadInt32(JsonNode? node, string errorCode)
    {
        if (node is JsonValue value && value.TryGetValue<int>(out var result))
        {
            return result;
        }

        throw new WorkerProtocolException(errorCode);
    }

    private static long ReadInt64(JsonNode? node, string errorCode)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var longResult) && longResult >= 0)
            {
                return longResult;
            }

            if (value.TryGetValue<int>(out var intResult) && intResult >= 0)
            {
                return intResult;
            }
        }

        throw new WorkerProtocolException(errorCode);
    }

    private static IReadOnlySet<string> ReadStringSet(JsonNode? node, string errorCode)
    {
        if (node is not JsonArray values)
        {
            throw new WorkerProtocolException(errorCode);
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var nodeValue in values)
        {
            if (nodeValue is not JsonValue value
                || !value.TryGetValue<string>(out var text)
                || string.IsNullOrWhiteSpace(text))
            {
                throw new WorkerProtocolException(errorCode);
            }

            result.Add(text);
        }

        return result;
    }

    private static bool IsTransportFailure(Exception error) =>
        error is WebSocketException
            or HttpRequestException
            or IOException
            or SocketException;

    private sealed record InboundFrame(WebSocketMessageType Type, byte[] Bytes);
}
