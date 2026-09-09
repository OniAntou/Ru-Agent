using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using RuBackend.Diagnostics;

[assembly: InternalsVisibleTo("RuBackend.Tests")]

namespace RuBackend.Worker;

public sealed class LoopbackWorkerClient :
    IWorkerConnectionClient,
    IRuntimeProtocolClient,
    IVoiceProtocolClient,
    IVtsProtocolClient
{
    private readonly DeviceWorkerClientOptions _options;
    private readonly ConcurrentDictionary<string, WorkerConnection> _activeRequests =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _poolLock = new(1, 1);
    private WorkerConnection? _pooledConnection;
    private WorkerHello? _pooledHello;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private int _disposed;

    public LoopbackWorkerClient(DeviceWorkerClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async Task<WorkerHello> ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var connection = new WorkerConnection(_options);
        return await connection.ConnectAsync(cancellationToken);
    }

    public async Task<VtsCatalog?> GetVtsCatalogAsync(
        string requestId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var request = new WorkerEnvelope(
            DeviceWorkerClientOptions.ProtocolVersion,
            requestId,
            WorkerMessageTypes.VtsCatalog,
            null,
            new JsonObject { ["operation"] = "catalog" },
            CorrelationContext.Current);

        try
        {
            var item = await SendPooledUnaryAsync(request, "vts", cancellationToken).ConfigureAwait(false);
            if (item.Type == WorkerMessageTypes.Error)
            {
                var error = RemoteError(item.Payload);
                if (error.Code is "vts_unavailable" or "vts_catalog_unavailable")
                {
                    return null;
                }

                throw error;
            }

            if (item.Type != WorkerMessageTypes.VtsCatalog)
            {
                throw new WorkerProtocolException("unexpected_type");
            }

            return new VtsCatalog(
                ReadVtsCodeSet(item.Payload["hotkey_codes"]),
                ReadVtsCodeSet(item.Payload["expression_codes"]));
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
    }

    public async Task TriggerVtsActionsAsync(
        string requestId,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var normalized = NormalizeVtsCodes(codes);
        var request = new WorkerEnvelope(
            DeviceWorkerClientOptions.ProtocolVersion,
            requestId,
            WorkerMessageTypes.VtsAction,
            null,
            new JsonObject
            {
                ["operation"] = "action",
                ["codes"] = new JsonArray(
                    normalized.Select(code => (JsonNode?)code).ToArray()),
            },
            CorrelationContext.Current);

        var item = await SendPooledUnaryAsync(request, "vts", cancellationToken).ConfigureAwait(false);
        if (item.Type == WorkerMessageTypes.Error)
        {
            throw RemoteError(item.Payload);
        }

        if (item.Type != WorkerMessageTypes.VtsAction
            || !string.Equals(
                ReadOptionalString(item.Payload["status"], "invalid_vts_action"),
                "ok",
                StringComparison.Ordinal))
        {
            throw new WorkerProtocolException("invalid_vts_action");
        }

        return;
    }

    public async Task<WorkerHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var request = new WorkerEnvelope(
            DeviceWorkerClientOptions.ProtocolVersion,
            $"health-{Guid.NewGuid():N}",
            WorkerMessageTypes.WorkerHealth,
            null,
            [],
            CorrelationContext.Current);

        var item = await SendPooledUnaryAsync(request, requiredCapability: null, cancellationToken).ConfigureAwait(false);
        if (item.Type == WorkerMessageTypes.Error)
        {
            throw RemoteError(item.Payload);
        }

        if (item.Type != WorkerMessageTypes.WorkerHealthOk)
        {
            throw new WorkerProtocolException("unexpected_type");
        }

        var ready = ReadBoolean(item.Payload["ready"], "invalid_health");
        var version = ReadInt32(item.Payload["version"], "invalid_health");
        var capabilities = ReadStringSet(item.Payload["capabilities"], "invalid_health");
        return new WorkerHealth(ready, version, capabilities);
    }

    public async Task<WorkerRuntimeResponse> InvokeRuntimeAsync(
        WorkerRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId)
            || string.IsNullOrWhiteSpace(request.Method)
            || string.IsNullOrWhiteSpace(request.PathAndQuery))
        {
            throw new WorkerProtocolException("invalid_runtime_request");
        }

        var envelope = new WorkerEnvelope(
            DeviceWorkerClientOptions.ProtocolVersion,
            request.RequestId,
            WorkerMessageTypes.RuntimeRequest,
            null,
            new JsonObject
            {
                ["method"] = request.Method,
                ["path_and_query"] = request.PathAndQuery,
                ["content_type"] = request.ContentType,
                // Binary-safe transport: body_b64 is authoritative. Legacy `body`
                // kept only for backward compat with workers that predate body_b64;
                // it carries the strict-UTF8 decoding when possible, else empty,
                // so we never corrupt binary via lossy GetString coercion.
                ["body_b64"] = Convert.ToBase64String(request.Body),
                ["body"] = EncodeLegacyBody(request.Body),
            },
            CorrelationContext.Current);

        var item = await SendPooledUnaryAsync(envelope, "runtime", cancellationToken).ConfigureAwait(false);
        if (item.Type == WorkerMessageTypes.Error)
        {
            throw RemoteError(item.Payload);
        }

        if (item.Type != WorkerMessageTypes.RuntimeResponse)
        {
            throw new WorkerProtocolException("unexpected_type");
        }

        var statusCode = ReadInt32(item.Payload["status_code"], "invalid_runtime_response");
        if (statusCode is < 100 or > 599)
        {
            throw new WorkerProtocolException("invalid_runtime_response");
        }

        var contentType = ReadOptionalString(
            item.Payload["content_type"],
            "invalid_runtime_response");
        var headers = ReadHeaders(item.Payload["headers"]);
        byte[] body;
        var b64 = ReadOptionalString(item.Payload["body_b64"], "invalid_runtime_response");
        if (!string.IsNullOrEmpty(b64))
        {
            try
            {
                body = Convert.FromBase64String(b64);
            }
            catch (FormatException)
            {
                throw new WorkerProtocolException("invalid_runtime_response");
            }
        }
        else if (!item.Binary.IsEmpty)
        {
            body = item.Binary.ToArray();
        }
        else
        {
            var text = ReadOptionalString(
                item.Payload["body"],
                "invalid_runtime_response") ?? string.Empty;
            body = Encoding.UTF8.GetBytes(text);
        }

        return new WorkerRuntimeResponse(statusCode, contentType, body, headers);
    }

    public async IAsyncEnumerable<WorkerEvent> StreamVoiceAsync(
        WorkerVoiceStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = new WorkerConnection(_options);
        var hello = await connection.ConnectAsync(cancellationToken);
        EnsureCapability(hello.Capabilities, "voice");
        if (!_activeRequests.TryAdd(request.RequestId, connection))
        {
            throw new WorkerProtocolException("duplicate_request");
        }

        using var receiveCancellation = new CancellationTokenSource();
        try
        {
            var envelope = new WorkerEnvelope(
                DeviceWorkerClientOptions.ProtocolVersion,
                request.RequestId,
                WorkerMessageTypes.VoiceStart,
                null,
                new JsonObject
                {
                    ["operation"] = "stream",
                    ["request_id"] = request.RequestId,
                    ["text"] = request.Text,
                    ["voice_label"] = request.VoiceLabel,
                    ["speed"] = request.Speed,
                    ["voice_style_id"] = request.VoiceStyleId,
                },
                CorrelationContext.Current);
            await using var enumerator = connection
                .SendRequestAsync(envelope, receiveCancellation.Token)
                .GetAsyncEnumerator();
            while (true)
            {
                var moveNext = enumerator.MoveNextAsync().AsTask();
                bool hasNext;
                try
                {
                    hasNext = await moveNext.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await TrySendVoiceCancelAsync(request.RequestId);
                    receiveCancellation.Cancel();
                    await IgnoreCancellationAsync(moveNext);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }

                if (!hasNext)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            receiveCancellation.Cancel();
            RemoveActiveRequest(request.RequestId, connection);
        }
    }

    public async Task SendVoiceSignalAsync(
        WorkerVoiceSignal signal,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(signal);
        var path = signal.Signal switch
        {
            "cancel" => "/_worker/voice/cancel",
            "playback" => "/_worker/voice/playback",
            _ => throw new WorkerProtocolException("invalid_voice_signal"),
        };
        var payload = new JsonObject
        {
            ["request_id"] = signal.RequestId,
            ["state"] = signal.State,
        };
        var response = await InvokeRuntimeAsync(
            new WorkerRuntimeRequest(
                $"voice-signal-{Guid.NewGuid():N}",
                "POST",
                path,
                "application/json",
                System.Text.Encoding.UTF8.GetBytes(payload.ToJsonString())),
            cancellationToken);
        if (response.StatusCode is < 200 or >= 300)
        {
            throw new WorkerProtocolException("voice_signal_failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var connections = _activeRequests.Values.Distinct().ToArray();
        _activeRequests.Clear();
        foreach (var connection in connections)
        {
            await connection.DisposeAsync();
        }

        WorkerConnection? pooled = null;
        await _poolLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            pooled = _pooledConnection;
            _pooledConnection = null;
            _pooledHello = null;
        }
        finally
        {
            _poolLock.Release();
        }

        if (pooled is not null)
        {
            await pooled.DisposeAsync().ConfigureAwait(false);
        }

        _poolLock.Dispose();
    }

    private static string EncodeLegacyBody(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return StrictUtf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            // Binary payload: legacy `body` cannot represent it without corruption,
            // so leave it empty and rely on body_b64 (authoritative).
            return string.Empty;
        }
    }

    private async Task<WorkerEvent> SendPooledUnaryAsync(
        WorkerEnvelope request,
        string? requiredCapability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        await _poolLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = _pooledConnection;
            var hello = _pooledHello;
            if (connection is null || hello is null)
            {
                var fresh = new WorkerConnection(_options);
                WorkerHello freshHello;
                try
                {
                    freshHello = await fresh.ConnectAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await fresh.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                _pooledConnection = fresh;
                _pooledHello = freshHello;
                connection = fresh;
                hello = freshHello;
            }

            if (requiredCapability is not null)
            {
                EnsureCapability(hello.Capabilities, requiredCapability);
            }

            try
            {
                await foreach (var item in connection.SendRequestAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    return item;
                }

                throw new WorkerProtocolException("missing_response");
            }
            catch (WorkerUnavailableException)
            {
                await InvalidatePooledLockedAsync(connection).ConfigureAwait(false);
                throw;
            }
            catch (WorkerProtocolException)
            {
                // Protocol errors may leave the stream out of sync; drop pooled
                // connection so the next call re-handshakes cleanly.
                await InvalidatePooledLockedAsync(connection).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _poolLock.Release();
        }
    }

    private async Task InvalidatePooledLockedAsync(WorkerConnection connection)
    {
        if (ReferenceEquals(_pooledConnection, connection))
        {
            _pooledConnection = null;
            _pooledHello = null;
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Dispose must not mask the original protocol/transport error.
            }
        }
    }

    private async Task TrySendVoiceCancelAsync(string requestId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await SendVoiceSignalAsync(
                new WorkerVoiceSignal(requestId, "cancel"),
                timeout.Token);
        }
        catch (Exception error) when (error is WorkerUnavailableException
            or WorkerProtocolException
            or OperationCanceledException)
        {
            // The caller's cancellation remains authoritative.
        }
    }

    private static async Task IgnoreCancellationAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (Exception error) when (error is OperationCanceledException
            or WorkerUnavailableException
            or WorkerProtocolException)
        {
        }
    }

    private static void EnsureCapability(
        IReadOnlySet<string> capabilities,
        string required)
    {
        if (!capabilities.Contains(required))
        {
            throw new WorkerProtocolException("unsupported_operation");
        }
    }

    private static WorkerProtocolException RemoteError(JsonObject payload)
    {
        var code = payload["code"] is JsonValue value
            && value.TryGetValue<string>(out var parsed)
            && !string.IsNullOrWhiteSpace(parsed)
                ? parsed
                : "worker_error";
        return new WorkerProtocolException(code);
    }

    private static string ReadString(JsonNode? node, string errorCode)
    {
        if (node is JsonValue value
            && value.TryGetValue<string>(out var result)
            && !string.IsNullOrWhiteSpace(result))
        {
            return result;
        }

        throw new WorkerProtocolException(errorCode);
    }

    private static string? ReadOptionalString(JsonNode? node, string errorCode)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var result))
        {
            return result;
        }

        throw new WorkerProtocolException(errorCode);
    }

    private static IReadOnlyDictionary<string, string> ReadHeaders(JsonNode? node)
    {
        if (node is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        if (node is not JsonObject values)
        {
            throw new WorkerProtocolException("invalid_runtime_response");
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            if (pair.Value is not JsonValue value
                || !value.TryGetValue<string>(out var text))
            {
                throw new WorkerProtocolException("invalid_runtime_response");
            }

            result[pair.Key] = text;
        }

        return result;
    }

    private static int ReadInt32(JsonNode? node, string errorCode)
    {
        if (node is JsonValue value && value.TryGetValue<int>(out var result))
        {
            return result;
        }

        throw new WorkerProtocolException(errorCode);
    }

    private static bool ReadBoolean(JsonNode? node, string errorCode)
    {
        if (node is JsonValue value && value.TryGetValue<bool>(out var result))
        {
            return result;
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
        foreach (var value in values)
        {
            result.Add(ReadString(value, errorCode));
        }

        return result;
    }

    private static IReadOnlySet<string> ReadVtsCodeSet(JsonNode? node)
    {
        if (node is not JsonArray values || values.Count > 512)
        {
            throw new WorkerProtocolException("invalid_vts_catalog");
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var code = ReadString(value, "invalid_vts_catalog");
            if (code.Length > 64 || !result.Add(code))
            {
                throw new WorkerProtocolException("invalid_vts_catalog");
            }
        }

        return result;
    }

    private static IReadOnlyList<string> NormalizeVtsCodes(IReadOnlyList<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        if (codes.Count is < 1 or > 3)
        {
            throw new WorkerProtocolException("invalid_request");
        }

        var normalized = codes
            .Select(code => code?.Trim().ToLowerInvariant() ?? string.Empty)
            .ToArray();
        if (normalized.Any(code => code.Length is < 1 or > 64)
            || normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new WorkerProtocolException("invalid_request");
        }

        return normalized;
    }

    private void RemoveActiveRequest(string requestId, WorkerConnection connection)
    {
        if (_activeRequests.TryGetValue(requestId, out var current)
            && ReferenceEquals(current, connection))
        {
            _activeRequests.TryRemove(requestId, out _);
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
