using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace RuAgent.Broker.Tools.LanguageServers;

public sealed class LspConnection : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _reader;
    private long _nextId;
    private int _disposed;
    private LspException? _terminalError;
    public event Action<JsonObject>? NotificationReceived;

    public LspConnection(Stream output, Stream input)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _reader = Task.Run(ReadLoopAsync);
    }

    internal Task ReaderCompletion => _reader;

    public async Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("lsp_request_id_collision");
        // The reader may have terminated before this request was registered, so fail now rather than wait for a signal that will never come.
        var terminal = Interlocked.CompareExchange(ref _terminalError, null, null);
        if (terminal is not null)
        {
            _pending.TryRemove(id, out _);
            throw new LspException(terminal.Code);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var sent = false;
        try
        {
            await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters.DeepClone() }, timeout.Token).ConfigureAwait(false);
            sent = true;
            try { return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                    throw new LspException("lsp_timeout");
                if (sent && !_lifetime.IsCancellationRequested) await TryCancelRequestAsync(id).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new LspException("lsp_timeout");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task TryCancelRequestAsync(long id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await NotifyAsync("$/cancelRequest", new JsonObject { ["id"] = id }, deadline.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or IOException or LspException or ObjectDisposedException) { }
    }

    public async Task NotifyAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try { await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters.DeepClone() }, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { throw new LspException("lsp_timeout"); }
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await LspFraming.WriteAsync(_input, bytes, cancellationToken).ConfigureAwait(false); }
        catch (IOException) { throw new LspException("lsp_server_unavailable"); }
        finally { _writeGate.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var bytes = await LspFraming.ReadAsync(_output, _lifetime.Token).ConfigureAwait(false);
                if (bytes is null) break;
                JsonObject? message;
                try { message = JsonNode.Parse(bytes) as JsonObject; }
                catch (System.Text.Json.JsonException) { throw new LspException("lsp_protocol_error"); }
                if (message is null) throw new LspException("lsp_protocol_error");
                if (message["method"] is JsonValue methodNode && methodNode.TryGetValue<string>(out var method))
                {
                    if (message.ContainsKey("id"))
                    {
                        await RespondToServerRequestAsync(message, method).ConfigureAwait(false);
                        continue;
                    }
                    if (message["params"] is JsonObject parameters) NotificationReceived?.Invoke(new JsonObject { ["method"] = method, ["params"] = parameters.DeepClone() });
                    continue;
                }
                if (message["id"] is not JsonValue idNode || !idNode.TryGetValue<long>(out var id) || !_pending.TryGetValue(id, out var task)) continue;
                if (message["error"] is JsonObject) task.TrySetException(new LspException("lsp_server_error"));
                else task.TrySetResult(message["result"]?.DeepClone());
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (LspException error) { Terminate(error); }
        catch (InvalidDataException error) { Terminate(new LspException(error.Message == "lsp_message_too_large" ? "lsp_message_too_large" : "lsp_protocol_error")); }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { Terminate(new LspException("lsp_server_unavailable")); }
        finally
        {
            Terminate(new LspException("lsp_server_unavailable"));
        }
    }

    // Interlocked.CompareExchange is a full fence. Paired with the read in RequestAsync after registration,
    // so either this FailPending sees the new request or the request sees the terminal error.
    private void Terminate(LspException error)
    {
        Interlocked.CompareExchange(ref _terminalError, error, null);
        FailPending(_terminalError!);
    }

    private void FailPending(LspException error)
    {
        foreach (var task in _pending.Values) task.TrySetException(error);
    }

    private Task RespondToServerRequestAsync(JsonObject request, string method)
    {
        var id = request["id"]?.DeepClone();
        if (id is null) throw new LspException("lsp_protocol_error");
        JsonObject response = method switch
        {
            "workspace/configuration" => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonArray() },
            "workspace/workspaceFolders" => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonArray() },
            "client/registerCapability" or "window/workDoneProgress/create" => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = null },
            _ => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not supported." } },
        };
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        return WriteResponseAsync(response, deadline);
    }

    private async Task WriteResponseAsync(JsonObject response, CancellationTokenSource deadline)
    {
        using (deadline) await WriteAsync(response, deadline.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (TimeoutException) { }
        // Active writes may still be unwinding cancellation through the gate.
        // Leave the managed semaphore alive rather than racing a concurrent WaitAsync.
        _lifetime.Dispose();
    }
}

public sealed class LspException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
