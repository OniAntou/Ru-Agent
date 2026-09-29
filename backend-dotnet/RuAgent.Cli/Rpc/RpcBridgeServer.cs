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

    /// <summary>Backpressure hook for RpcRunBridge: never blocks the SSE pump.</summary>
    public bool TryPublish(JsonObject notification)
    {
        if (_backpressured)
        {
            return false;
        }

        if (_outbound.Writer.TryWrite(notification))
        {
            return true;
        }

        _backpressured = true;
        return false;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var reason = "stdin_closed";
        var exitCode = 0;
        var writerTask = WriterLoopAsync(cancellationToken);
        try
        {
            while (true)
            {
                var frame = await RpcFrameCodec.ReadFrameAsync(_input, RpcProtocol.MaxFrameCharacters, cancellationToken).ConfigureAwait(false);
                if (frame.Status == RpcFrameStatus.EndOfInput)
                {
                    break;
                }

                if (frame.Status == RpcFrameStatus.TooLarge)
                {
                    await _outbound.Writer.WriteAsync(
                        RpcMessage.ErrorResponse(null, RpcProtocol.InvalidRequest, "request_too_large: frame exceeds 1 MiB."),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(frame.Line))
                {
                    continue;
                }

                var request = RpcMessage.TryParseRequest(frame.Line, out var parseError);
                if (request is null)
                {
                    await _outbound.Writer.WriteAsync(parseError!, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (request.Method == "$/cancelRequest")
                {
                    // v1: every request is short-lived; there is no long-running request to cancel.
                    continue;
                }

                try
                {
                    var result = await _dispatcher.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
                    if (!request.IsNotification)
                    {
                        await _outbound.Writer.WriteAsync(
                            RpcMessage.SuccessResponse(request.Id, result),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (RpcDispatchException ex)
                {
                    if (!request.IsNotification)
                    {
                        await _outbound.Writer.WriteAsync(
                            RpcMessage.ErrorResponse(request.Id, ex.RpcErrorCode, ex.Message, ex.RuCode, ex.HttpStatus),
                            cancellationToken).ConfigureAwait(false);
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
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = "cancelled";
            exitCode = 130;
        }
        finally
        {
            if (_backpressured)
            {
                reason = "backpressure";
                exitCode = 3;
            }

            _outbound.Writer.TryWrite(RpcMessage.Notification("bridge.shutdown", new JsonObject { ["reason"] = reason }));
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
