using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using RuBackend.Diagnostics;

namespace RuBackend.Worker;

public sealed class LoopbackWorkerClient : IWorkerClient, IRuntimeProtocolClient, IVoiceProtocolClient
{
    private readonly WorkerClientOptions _options;
    private readonly ConcurrentDictionary<string, WorkerConnection> _activeRequests =
        new(StringComparer.Ordinal);
    private int _disposed;

    public LoopbackWorkerClient(WorkerClientOptions options)
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

    public async IAsyncEnumerable<WorkerEvent> GenerateChatAsync(
        WorkerChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        WorkerEnvelopeCodec.Validate(new WorkerEnvelope(
            _options.ProtocolVersion,
            request.RequestId,
            WorkerMessageTypes.ChatStart,
            null,
            [],
            CorrelationContext.Current));

        await using var connection = new WorkerConnection(_options);
        var hello = await connection.ConnectAsync(cancellationToken);
        EnsureCapability(hello.Capabilities, "chat");
        if (!_activeRequests.TryAdd(request.RequestId, connection))
        {
            throw new WorkerProtocolException("duplicate_request");
        }

        using var receiveCancellation = new CancellationTokenSource();
        try
        {
            var envelope = new WorkerEnvelope(
                _options.ProtocolVersion,
                request.RequestId,
                WorkerMessageTypes.ChatStart,
                null,
                BuildChatPayload(request),
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
                    await TrySendCancellationAsync(connection, request.RequestId);
                    receiveCancellation.Cancel();
                    await IgnoreCancellationAsync(moveNext);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }

                if (!hasNext)
                {
                    yield break;
                }

                var item = enumerator.Current;
                if (item.Type == WorkerMessageTypes.ChatToolRequest
                    && request.ToolHandler is not null)
                {
                    var toolRequest = ParseToolRequest(item.Payload);
                    var result = await request.ToolHandler(toolRequest, cancellationToken);
                    await connection.SendEnvelopeAsync(
                        ToolResultEnvelope(request.RequestId, toolRequest, result),
                        cancellationToken);
                }

                yield return item;
            }
        }
        finally
        {
            receiveCancellation.Cancel();
            RemoveActiveRequest(request.RequestId, connection);
        }
    }

    public async Task<IReadOnlyList<WorkerFact>> ExtractFactsAsync(
        string requestId,
        string prompt,
        string response,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var connection = new WorkerConnection(_options);
        var hello = await connection.ConnectAsync(cancellationToken);
        EnsureCapability(hello.Capabilities, "memory.extract");
        var request = new WorkerEnvelope(
            _options.ProtocolVersion,
            requestId,
            WorkerMessageTypes.MemoryExtract,
            null,
            new JsonObject
            {
                ["prompt"] = prompt,
                ["response"] = response,
            },
            CorrelationContext.Current);

        await foreach (var item in connection.SendRequestAsync(request, cancellationToken))
        {
            if (item.Type == WorkerMessageTypes.Error)
            {
                throw RemoteError(item.Payload);
            }

            if (item.Type != WorkerMessageTypes.MemoryFacts)
            {
                throw new WorkerProtocolException("unexpected_type");
            }

            return ParseFacts(item.Payload);
        }

        throw new WorkerProtocolException("missing_response");
    }

    public async Task<VtsCatalog?> GetVtsCatalogAsync(
        string requestId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var connection = new WorkerConnection(_options);
        var hello = await connection.ConnectAsync(cancellationToken);
        EnsureCapability(hello.Capabilities, "vts");
        var request = new WorkerEnvelope(
            _options.ProtocolVersion,
            requestId,
            WorkerMessageTypes.VtsCatalog,
            null,
            new JsonObject { ["operation"] = "catalog" },
            CorrelationContext.Current);

        await foreach (var item in connection.SendRequestAsync(request, cancellationToken))
        {
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

        throw new WorkerProtocolException("missing_response");
    }

    public async Task TriggerVtsActionsAsync(
        string requestId,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var normalized = NormalizeVtsCodes(codes);
        await using var connection = new WorkerConnection(_options);
        var hello = await connection.ConnectAsync(cancellationToken);
        EnsureCapability(hello.Capabilities, "vts");
        var request = new WorkerEnvelope(
            _options.ProtocolVersion,
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

        await foreach (var item in connection.SendRequestAsync(request, cancellationToken))
        {
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

        throw new WorkerProtocolException("missing_response");
    }

    public async Task CancelAsync(string requestId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_activeRequests.TryGetValue(requestId, out var connection))
        {
            await connection.SendEnvelopeAsync(
                CancellationEnvelope(requestId),
                cancellationToken);
        }
    }

    public async Task<WorkerHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var connection = new WorkerConnection(_options);
        await connection.ConnectAsync(cancellationToken);
        var request = new WorkerEnvelope(
            _options.ProtocolVersion,
            $"health-{Guid.NewGuid():N}",
            WorkerMessageTypes.WorkerHealth,
            null,
            [],
            CorrelationContext.Current);

        await foreach (var item in connection.SendRequestAsync(request, cancellationToken))
        {
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

        throw new WorkerProtocolException("missing_response");
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

        await using var connection = new WorkerConnection(_options);
        var hello = await connection.ConnectAsync(cancellationToken);
        EnsureCapability(hello.Capabilities, "runtime");
        var envelope = new WorkerEnvelope(
            _options.ProtocolVersion,
            request.RequestId,
            WorkerMessageTypes.RuntimeRequest,
            null,
            new JsonObject
            {
                ["method"] = request.Method,
                ["path_and_query"] = request.PathAndQuery,
                ["content_type"] = request.ContentType,
                ["body"] = System.Text.Encoding.UTF8.GetString(request.Body),
            },
            CorrelationContext.Current);

        await foreach (var item in connection.SendRequestAsync(envelope, cancellationToken))
        {
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
            if (!item.Binary.IsEmpty)
            {
                body = item.Binary.ToArray();
            }
            else
            {
                var text = ReadOptionalString(
                    item.Payload["body"],
                    "invalid_runtime_response") ?? string.Empty;
                body = System.Text.Encoding.UTF8.GetBytes(text);
            }

            return new WorkerRuntimeResponse(statusCode, contentType, body, headers);
        }

        throw new WorkerProtocolException("missing_response");
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
                _options.ProtocolVersion,
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
    }

    private static JsonObject BuildChatPayload(WorkerChatRequest request)
    {
        var turns = new JsonArray();
        foreach (var turn in request.Turns)
        {
            turns.Add(new JsonObject
            {
                ["prompt"] = turn.Prompt,
                ["response"] = turn.Response,
            });
        }

        return new JsonObject
        {
            ["prompt"] = request.Prompt,
            ["session_id"] = request.SessionId,
            ["turns"] = turns,
            ["memory_context"] = request.MemoryContext,
            ["vts_instruction"] = request.VtsInstruction,
            ["settings"] = request.Settings.DeepClone(),
        };
    }

    private static IReadOnlyList<WorkerFact> ParseFacts(JsonObject payload)
    {
        if (payload["facts"] is not JsonArray values)
        {
            throw new WorkerProtocolException("invalid_memory_facts");
        }

        var result = new List<WorkerFact>(values.Count);
        foreach (var value in values)
        {
            if (value is not JsonObject fact)
            {
                throw new WorkerProtocolException("invalid_memory_facts");
            }

            result.Add(new WorkerFact(
                ReadString(fact["kind"], "invalid_memory_facts"),
                ReadString(fact["content"], "invalid_memory_facts"),
                ReadOptionalString(fact["scope"], "invalid_memory_facts"),
                ReadDouble(fact["confidence"], "invalid_memory_facts"),
                ReadDouble(fact["importance"], "invalid_memory_facts")));
        }

        return result;
    }

    private static WorkerEnvelope CancellationEnvelope(string requestId) =>
        new(
            WorkerClientOptions.CurrentProtocolVersion,
            requestId,
            WorkerMessageTypes.ChatCancel,
            null,
            []);

    private static WorkerEnvelope ToolResultEnvelope(
        string parentRequestId,
        WorkerToolRequest request,
        WorkerToolResult result) =>
        new(
            WorkerClientOptions.CurrentProtocolVersion,
            parentRequestId,
            WorkerMessageTypes.ChatToolResult,
            null,
            new JsonObject
            {
                ["request_id"] = result.RequestId,
                ["session_id"] = request.SessionId,
                ["correlation_id"] = request.CorrelationId,
                ["status"] = result.Status,
                ["output"] = result.Output.DeepClone(),
                ["error_code"] = result.ErrorCode,
                ["safe_summary"] = result.SafeSummary,
            });

    private static WorkerToolRequest ParseToolRequest(JsonObject payload)
    {
        if (payload["arguments"] is not JsonObject arguments)
        {
            throw new WorkerProtocolException("invalid_tool_request");
        }

        return new WorkerToolRequest(
            ReadString(payload["request_id"], "invalid_tool_request"),
            ReadString(payload["session_id"], "invalid_tool_request"),
            ReadString(payload["correlation_id"], "invalid_tool_request"),
            ReadString(payload["tool_id"], "invalid_tool_request"),
            ReadString(payload["tool_version"], "invalid_tool_request"),
            arguments.DeepClone().AsObject(),
            ReadInt32(payload["deadline_ms"], "invalid_tool_request"));
    }

    private static async Task TrySendCancellationAsync(
        WorkerConnection connection,
        string requestId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await connection.SendEnvelopeAsync(CancellationEnvelope(requestId), timeout.Token);
        }
        catch (Exception error) when (error is WorkerUnavailableException
            or WorkerProtocolException
            or OperationCanceledException)
        {
            // The caller's cancellation remains authoritative.
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

    private static double ReadDouble(JsonNode? node, string errorCode)
    {
        if (node is JsonValue value && value.TryGetValue<double>(out var result))
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
