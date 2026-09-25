using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using RuAgent.Harness.Core;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Runtime.Worker;

public sealed class WorkerV2Server
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _token;
    private readonly WorkerV2StepService _steps;

    public WorkerV2Server(string token, WorkerV2StepService steps)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Worker token is required.", nameof(token));
        }

        _token = token;
        _steps = steps ?? throw new ArgumentNullException(nameof(steps));
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!IsWorkerPath(context.Request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        await HandleConnectionAsync(socket, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task HandleConnectionAsync(WebSocket socket, CancellationToken connectionCancellation)
    {
        var sendLock = new SemaphoreSlim(1, 1);
        // Written by the connection loop and removed by the request task when it settles, so
        // it is reached from two threads. A plain Dictionary corrupts under that (the request
        // finally block can remove while the loop is adding), which is also why removals below
        // go through TryRemove rather than the IDictionary view.
        var requestTasks = new ConcurrentDictionary<string, Task>(StringComparer.Ordinal);
        // Same two-thread shape as requestTasks: the connection loop adds and
        // cancels while the settling request task removes. A plain Dictionary
        // corrupts under that, so this one is concurrent too, and the cancel
        // path below tolerates losing the race against the task's Dispose.
        var cancellations = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        // Duplicate-request detection and per-request frame sequence, bounded so a
        // long-lived connection cannot grow per-request state without limit.
        var history = new WorkerRequestHistory();

        try
        {
            string? helloFrame;
            try
            {
                helloFrame = await ReceiveTextAsync(socket, connectionCancellation).ConfigureAwait(false);
            }
            catch (AgentProtocolException error)
            {
                await SendErrorAsync(socket, sendLock, "connection", error.Code, false, history.NextSequence, connectionCancellation).ConfigureAwait(false);
                await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, connectionCancellation).ConfigureAwait(false);
                return;
            }

            if (helloFrame is null)
            {
                return;
            }

            WorkerV2Envelope hello;
            try
            {
                hello = WorkerV2EnvelopeCodec.Parse(helloFrame);
            }
            catch (AgentProtocolException error)
            {
                await SendErrorAsync(socket, sendLock, "connection", error.Code, false, history.NextSequence, connectionCancellation).ConfigureAwait(false);
                await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, connectionCancellation).ConfigureAwait(false);
                return;
            }

            if (hello.Type != WorkerV2Contracts.Hello)
            {
                await SendErrorAsync(socket, sendLock, hello.Id, "hello_required", false, history.NextSequence, connectionCancellation).ConfigureAwait(false);
                await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, connectionCancellation).ConfigureAwait(false);
                return;
            }

            var suppliedToken = hello.Payload["token"]?.GetValue<string>();
            if (suppliedToken is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(suppliedToken), Encoding.UTF8.GetBytes(_token)))
            {
                await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, connectionCancellation).ConfigureAwait(false);
                return;
            }

            await SendAsync(socket, sendLock, new WorkerV2Envelope(
                WorkerV2Contracts.ProtocolVersion,
                hello.Id,
                WorkerV2Contracts.HelloOk,
                0,
                new JsonObject
                {
                    ["version"] = WorkerV2Contracts.ProtocolVersion,
                    ["capabilities"] = new JsonArray("agent.step"),
                }), connectionCancellation).ConfigureAwait(false);

            while (socket.State == WebSocketState.Open && !connectionCancellation.IsCancellationRequested)
            {
                string? frame;
                try
                {
                    frame = await ReceiveTextAsync(socket, connectionCancellation).ConfigureAwait(false);
                }
                catch (AgentProtocolException error)
                {
                    await SendErrorAsync(socket, sendLock, "connection", error.Code, false, history.NextSequence, connectionCancellation).ConfigureAwait(false);
                    continue;
                }

                if (frame is null)
                {
                    break;
                }

                WorkerV2Envelope envelope;
                try
                {
                    envelope = WorkerV2EnvelopeCodec.Parse(frame);
                }
                catch (AgentProtocolException error)
                {
                    await SendErrorAsync(socket, sendLock, "connection", error.Code, false, history.NextSequence, connectionCancellation).ConfigureAwait(false);
                    continue;
                }

                if (envelope.Type == WorkerV2Contracts.AgentStepCancel)
                {
                    if (!cancellations.TryGetValue(envelope.Id, out var cancellation))
                    {
                        await SendErrorAsync(socket, sendLock, envelope.Id, "request_not_found", false, history.NextSequence, connectionCancellation).ConfigureAwait(false);
                    }
                    else
                    {
                        try
                        {
                            cancellation.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                            // The request settled between the lookup and the
                            // cancel and disposed its source; cancelling a
                            // finished request is a no-op, not an error.
                        }
                    }

                    continue;
                }

                if (envelope.Type != WorkerV2Contracts.AgentStepStart)
                {
                    await SendErrorAsync(socket, sendLock, envelope.Id, "unsupported_operation", false, history.NextSequence, connectionCancellation).ConfigureAwait(false);
                    continue;
                }

                if (!history.TryBegin(envelope.Id))
                {
                    await SendErrorAsync(socket, sendLock, envelope.Id, "duplicate_request", false, history.NextSequence, connectionCancellation).ConfigureAwait(false);
                    continue;
                }

                if (requestTasks.Count > 0)
                {
                    await SendErrorAsync(socket, sendLock, envelope.Id, "request_in_progress", false, history.NextSequence, connectionCancellation).ConfigureAwait(false);
                    continue;
                }

                var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(connectionCancellation);
                cancellations[envelope.Id] = cancellationSource;
                var task = RunRequestAsync(socket, sendLock, envelope, cancellationSource, history.NextSequence, requestTasks, cancellations);
                requestTasks[envelope.Id] = task;
                // The request task removes its own entry when it settles. If it settled
                // before the store above (fully synchronous completion), that removal has
                // already run and this completed task would otherwise sit in the map for
                // the life of the connection, wedging the request_in_progress gate.
                if (task.IsCompleted)
                {
                    requestTasks.TryRemove(envelope.Id, out _);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Connection shutdown is a normal lifecycle path.
        }
        catch (WebSocketException)
        {
            // The peer went away; no error payload can be delivered safely.
        }
        finally
        {
            // Snapshot both maps: a settling request removes its own entry, and enumerating
            // the live dictionary while it does would throw out of this cleanup. Cancel and
            // Dispose here can still meet a request task that finished concurrently, so the
            // disposed case is swallowed -- cancelling an already-settled request is a no-op.
            foreach (var cancellation in cancellations.Values.ToArray())
            {
                try { cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            var tasks = requestTasks.Values.ToArray();
            if (tasks.Length > 0)
            {
                // Cannot throw: a request task sends its error frames through
                // TrySendErrorAsync and funnels every failure into catch(Exception),
                // so it always settles without faulting.
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            foreach (var cancellation in cancellations.Values.ToArray())
            {
                try { cancellation.Dispose(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    private async Task RunRequestAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        WorkerV2Envelope envelope,
        CancellationTokenSource cancellationSource,
        Func<string, long> nextSequence,
        ConcurrentDictionary<string, Task> requestTasks,
        ConcurrentDictionary<string, CancellationTokenSource> cancellations)
    {
        try
        {
            async ValueTask PublishDeltaAsync(string text)
            {
                await SendOrderedAsync(
                    socket,
                    sendLock,
                    envelope.Id,
                    WorkerV2Contracts.AgentStepDelta,
                    envelope.CorrelationId,
                    new JsonObject { ["text"] = text },
                    nextSequence,
                    cancellationSource.Token).ConfigureAwait(false);
            }

            var result = await _steps.RunAsync(
                envelope.Payload,
                PublishDeltaAsync,
                cancellationSource.Token,
                modelStepId: envelope.Id).ConfigureAwait(false);
            var toolCalls = new JsonArray();
            foreach (var call in result.ToolCalls)
            {
                toolCalls.Add(new JsonObject
                {
                    ["provider_call_id"] = call.ProviderCallId,
                    ["tool_id"] = call.ToolId,
                    ["tool_version"] = call.ToolVersion,
                    ["arguments"] = JsonNode.Parse(call.Arguments.GetRawText()),
                });
            }

            var donePayload = new JsonObject
            {
                ["text"] = result.Text,
                ["tool_calls"] = toolCalls,
                ["finish_reason"] = result.FinishReason,
                ["prompt_receipt"] = WorkerV2PromptCodec.WriteReceipt(result.PromptReceipt),
            };
            if (result.Usage is not null)
            {
                donePayload["usage"] = UsageJson(result.Usage);
            }
            if (!string.IsNullOrWhiteSpace(result.ReasoningText))
            {
                donePayload["reasoning_text"] = result.ReasoningText;
            }

            await SendOrderedAsync(
                socket,
                sendLock,
                envelope.Id,
                WorkerV2Contracts.AgentStepDone,
                envelope.CorrelationId,
                donePayload,
                nextSequence,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TrySendErrorAsync(socket, sendLock, envelope.Id, "cancelled", false, nextSequence, CancellationToken.None).ConfigureAwait(false);
        }
        catch (WorkerV2StepException error)
        {
            await TrySendErrorAsync(socket, sendLock, envelope.Id, error.Code, error.Retryable, nextSequence, CancellationToken.None).ConfigureAwait(false);
        }
        catch (AgentProtocolException)
        {
            await TrySendErrorAsync(socket, sendLock, envelope.Id, "protocol_failure", false, nextSequence, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await TrySendErrorAsync(socket, sendLock, envelope.Id, "generation_failed", true, nextSequence, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            requestTasks.TryRemove(envelope.Id, out _);
            if (cancellations.TryRemove(envelope.Id, out var owned))
            {
                owned.Dispose();
            }
        }
    }

    private static async Task TrySendErrorAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        string id,
        string code,
        bool retryable,
        Func<string, long> nextSequence,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendErrorAsync(socket, sendLock, id, code, retryable, nextSequence, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The peer is gone, so the error frame is undeliverable. A request
            // task must never fault: connection cleanup awaits every request
            // task and a fault here would escape into the teardown path.
        }
    }

    private static async Task SendErrorAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        string id,
        string code,
        bool retryable,
        Func<string, long> nextSequence,
        CancellationToken cancellationToken)
    {
        await SendOrderedAsync(
            socket,
            sendLock,
            id,
            WorkerV2Contracts.Error,
            null,
            new JsonObject
            {
                ["code"] = code,
                ["retryable"] = retryable,
            },
            nextSequence,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsWorkerPath(PathString path) =>
        string.Equals(path, "/worker/v2", StringComparison.Ordinal);

    private static JsonObject UsageJson(ModelUsage usage)
    {
        var json = new JsonObject();
        if (usage.InputTokens is { } input) json["input_tokens"] = input;
        if (usage.OutputTokens is { } output) json["output_tokens"] = output;
        if (usage.TotalTokens is { } total) json["total_tokens"] = total;
        if (usage.CacheReadInputTokens is { } read) json["cache_read_input_tokens"] = read;
        if (usage.CacheWriteInputTokens is { } write) json["cache_write_input_tokens"] = write;
        return json;
    }

    private static async Task SendAsync(WebSocket socket, SemaphoreSlim sendLock, WorkerV2Envelope envelope, CancellationToken cancellationToken)
    {
        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendCoreAsync(socket, envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <summary>
    /// Assigns the per-request sequence number inside the send lock so a frame
    /// can never consume a number without being written: the Host requires the
    /// exact 1..N order, and an allocation racing with an aborted send would
    /// otherwise surface as <c>invalid_sequence</c>.
    /// </summary>
    private static async Task SendOrderedAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        string id,
        string type,
        string? correlationId,
        JsonObject payload,
        Func<string, long> nextSequence,
        CancellationToken cancellationToken)
    {
        // Validate/serialize with a placeholder sequence first: a rejected frame
        // must fail before a sequence number is consumed, otherwise the next
        // frame leaves a hole and the Host reports invalid_sequence instead of
        // the real rejection.
        _ = WorkerV2EnvelopeCodec.Serialize(new WorkerV2Envelope(
            WorkerV2Contracts.ProtocolVersion,
            id,
            type,
            1,
            correlationId,
            payload));

        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = new WorkerV2Envelope(
                WorkerV2Contracts.ProtocolVersion,
                id,
                type,
                nextSequence(id),
                correlationId,
                payload);
            await SendCoreAsync(socket, envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static Task SendCoreAsync(WebSocket socket, WorkerV2Envelope envelope, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(WorkerV2EnvelopeCodec.Serialize(envelope));
        return socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new AgentProtocolException("text_control_required");
            }

            stream.Write(buffer, 0, result.Count);
            if (stream.Length > WorkerV2Contracts.MaxFrameBytes)
            {
                throw new AgentProtocolException("message_too_large");
            }

            if (result.EndOfMessage)
            {
                try
                {
                    return StrictUtf8.GetString(stream.ToArray());
                }
                catch (DecoderFallbackException error)
                {
                    throw new AgentProtocolException("invalid_utf8", error.Message);
                }
            }
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseAsync(status, status.ToString(), cancellationToken).ConfigureAwait(false);
        }
    }
}
