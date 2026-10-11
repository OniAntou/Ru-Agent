using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace RuAgent.Cli.Rpc;

/// <summary>
/// The stdio loop: reads frames from stdin, dispatches them sequentially and
/// writes responses and notifications through one writer task. A notification
/// that cannot enter the bounded queue is a fail-closed condition: the bridge
/// stops instead of dropping a durable event silently.
/// </summary>
public sealed class RpcBridgeServer
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _log;
    private readonly RpcDispatcher _dispatcher;
    private readonly Channel<JsonObject> _outbound;
    private bool _backpressured;

    // Cancels the read loop when backpressure ends the session, so RunAsync's own
    // shutdown path can drain the queue, deliver bridge.shutdown in order, and
    // report exit code 3. The pump thread must never kill the process itself:
    // Environment.Exit there dropped every queued frame and the reason it carried.
    private readonly CancellationTokenSource _shutdown = new();

    // Written by the reader thread when the channel is closing (RunAsync's finally)
    // and read by the SSE pump thread in TryPublish; volatile so a pump thread
    // cannot miss a clean shutdown and misfire the fail-closed terminate.
    private volatile bool _writerCompleted;

    public RpcBridgeServer(
        TextReader input,
        TextWriter output,
        TextWriter log,
        RpcDispatcher dispatcher,
        IRunBridge runBridge,
        int outboundCapacity = RpcProtocol.MaxOutboundMessages)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _ = runBridge ?? throw new ArgumentNullException(nameof(runBridge));
        _outbound = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(outboundCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>
    /// Backpressure hook for RpcRunBridge: never blocks the SSE pump. A
    /// notification that cannot enter the bounded queue is fail-closed: the
    /// session ends with exit code 3 instead of dropping a durable event.
    /// </summary>
    public bool TryPublish(JsonObject notification)
    {
        if (_backpressured || _writerCompleted)
        {
            return false;
        }

        if (_outbound.Writer.TryWrite(notification))
        {
            return true;
        }

        // Fail closed: a full queue means a durable notification cannot be
        // delivered. Stopping the session lets RunAsync drain what is already
        // queued and flush the shutdown frame before the process exits, so the
        // client learns the reason instead of seeing frames vanish.
        _backpressured = true;
        _shutdown.Cancel();
        return false;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        var token = linked.Token;
        var reason = "stdin_closed";
        var exitCode = 0;
        var writerTask = WriterLoopAsync(token);
        var frameReader = new RpcFrameReader(_input, RpcProtocol.MaxFrameBytes);
        try
        {
            while (true)
            {
                var frame = await frameReader.ReadFrameAsync(token).ConfigureAwait(false);
                if (frame.Status == RpcFrameStatus.EndOfInput)
                {
                    break;
                }

                if (frame.Status == RpcFrameStatus.TooLarge)
                {
                    await _outbound.Writer.WriteAsync(
                        RpcMessage.ErrorResponse(null, RpcProtocol.InvalidRequest, "request_too_large: frame exceeds 1 MiB."),
                        token).ConfigureAwait(false);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(frame.Line))
                {
                    continue;
                }

                var request = RpcMessage.TryParseRequest(frame.Line, out var parseError);
                if (request is null)
                {
                    await _outbound.Writer.WriteAsync(parseError!, token).ConfigureAwait(false);
                    continue;
                }

                if (request.Method == "$/cancelRequest")
                {
                    // v1: every request is short-lived; there is no long-running request to cancel.
                    continue;
                }

                try
                {
                    var result = await _dispatcher.DispatchAsync(request, token).ConfigureAwait(false);
                    if (!request.IsNotification)
                    {
                        await _outbound.Writer.WriteAsync(
                            RpcMessage.SuccessResponse(request.Id, result),
                            token).ConfigureAwait(false);
                    }
                }
                catch (RpcDispatchException ex)
                {
                    if (!request.IsNotification)
                    {
                        await _outbound.Writer.WriteAsync(
                            RpcMessage.ErrorResponse(request.Id, ex.RpcErrorCode, ex.Message, ex.RuCode, ex.HttpStatus),
                            token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.WriteLine($"internal_error: {ex.Message}");
                    if (!request.IsNotification)
                    {
                        await _outbound.Writer.WriteAsync(
                            RpcMessage.ErrorResponse(request.Id, RpcProtocol.InternalError, "internal_error: the bridge failed to complete the request."),
                            token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = _backpressured ? "backpressure" : "cancelled";
            exitCode = _backpressured ? 3 : 130;
        }
        finally
        {
            if (_backpressured)
            {
                reason = "backpressure";
                exitCode = 3;
            }

            // A pump that ends during shutdown must not be mistaken for a full
            // queue: the channel is closing, so there is nothing left to fail.
            _writerCompleted = true;
            await TryWriteShutdownAsync(reason).ConfigureAwait(false);
            _outbound.Writer.TryComplete();
        }

        try
        {
            await writerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation stops the reader, not the writer: the queued messages
            // (including bridge.shutdown) are still delivered best-effort, so the
            // computed exit code is the result rather than the writer's OCE.
        }

        return exitCode;
    }

    /// <summary>
    /// Queues the shutdown frame, waiting briefly for room when the queue is
    /// full: the writer is draining concurrently, so the wait is normally
    /// instantaneous, and a client that is gone only means the exit code is
    /// what reports the reason.
    /// </summary>
    private async Task TryWriteShutdownAsync(string reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _outbound.Writer.WriteAsync(
                RpcMessage.Notification("bridge.shutdown", new JsonObject { ["reason"] = reason }),
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort by contract; the exit code still carries the reason.
        }
    }

    private async Task WriterLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var message in _outbound.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                await RpcFrameCodec.WriteFrameAsync(_output, message, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // WriteFrameAsync throws before writing anything when the token is
                // canceled, so one retry without the token delivers the frame exactly
                // once and keeps bridge.shutdown on the wire during cancellation.
                await RpcFrameCodec.WriteFrameAsync(_output, message, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
