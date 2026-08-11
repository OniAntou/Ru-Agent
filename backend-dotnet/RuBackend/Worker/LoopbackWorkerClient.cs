using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace RuBackend.Worker;

public sealed class LoopbackWorkerClient : IWorkerClient
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
            []));

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
                BuildChatPayload(request));
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

                yield return enumerator.Current;
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
            });

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
            []);

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
