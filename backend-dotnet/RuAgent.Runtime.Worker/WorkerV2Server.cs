using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Runtime.Worker;

public sealed class WorkerV2Server
{
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
        if (!string.Equals(context.Request.Path, "/worker/v2", StringComparison.Ordinal))
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
        var requestTasks = new Dictionary<string, Task>(StringComparer.Ordinal);
        var cancellations = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var seenRequests = new HashSet<string>(StringComparer.Ordinal);
        var sequences = new Dictionary<string, long>(StringComparer.Ordinal);

        long NextSequence(string id)
        {
            var next = sequences.TryGetValue(id, out var value) ? value : 1;
            sequences[id] = checked(next + 1);
            return next;
        }

        try
        {
            var helloFrame = await ReceiveTextAsync(socket, connectionCancellation).ConfigureAwait(false);
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
                await SendErrorAsync(socket, sendLock, "connection", error.Code, false, NextSequence("connection"), connectionCancellation).ConfigureAwait(false);
                await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, connectionCancellation).ConfigureAwait(false);
                return;
            }

            if (hello.Type != WorkerV2Contracts.Hello)
            {
                await SendErrorAsync(socket, sendLock, hello.Id, "hello_required", false, NextSequence(hello.Id), connectionCancellation).ConfigureAwait(false);
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
                var frame = await ReceiveTextAsync(socket, connectionCancellation).ConfigureAwait(false);
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
                    await SendErrorAsync(socket, sendLock, "connection", error.Code, false, NextSequence("connection"), connectionCancellation).ConfigureAwait(false);
                    continue;
                }

                if (envelope.Type == WorkerV2Contracts.AgentStepCancel)
                {
                    if (!cancellations.TryGetValue(envelope.Id, out var cancellation))
                    {
                        await SendErrorAsync(socket, sendLock, envelope.Id, "request_not_found", false, NextSequence(envelope.Id), connectionCancellation).ConfigureAwait(false);
                    }
                    else
                    {
                        cancellation.Cancel();
                    }

                    continue;
                }

                if (envelope.Type != WorkerV2Contracts.AgentStepStart)
                {
                    await SendErrorAsync(socket, sendLock, envelope.Id, "unsupported_operation", false, NextSequence(envelope.Id), connectionCancellation).ConfigureAwait(false);
                    continue;
                }

                if (!seenRequests.Add(envelope.Id))
                {
                    await SendErrorAsync(socket, sendLock, envelope.Id, "duplicate_request", false, NextSequence(envelope.Id), connectionCancellation).ConfigureAwait(false);
                    continue;
                }

                if (requestTasks.Count > 0)
                {
                    await SendErrorAsync(socket, sendLock, envelope.Id, "request_in_progress", false, NextSequence(envelope.Id), connectionCancellation).ConfigureAwait(false);
                    continue;
                }

                var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(connectionCancellation);
                cancellations[envelope.Id] = cancellationSource;
                var task = RunRequestAsync(socket, sendLock, envelope, cancellationSource, NextSequence, requestTasks, cancellations);
                requestTasks[envelope.Id] = task;
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
            foreach (var cancellation in cancellations.Values)
            {
                cancellation.Cancel();
            }

            if (requestTasks.Count > 0)
            {
                await Task.WhenAll(requestTasks.Values).ConfigureAwait(false);
            }

            foreach (var cancellation in cancellations.Values)
            {
                cancellation.Dispose();
            }
        }
    }

    private async Task RunRequestAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        WorkerV2Envelope envelope,
        CancellationTokenSource cancellationSource,
        Func<string, long> nextSequence,
        IDictionary<string, Task> requestTasks,
        IDictionary<string, CancellationTokenSource> cancellations)
    {
        try
        {
            var result = await _steps.RunAsync(envelope.Payload, cancellationSource.Token).ConfigureAwait(false);
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

            await SendAsync(socket, sendLock, new WorkerV2Envelope(
                WorkerV2Contracts.ProtocolVersion,
                envelope.Id,
                WorkerV2Contracts.AgentStepDone,
                nextSequence(envelope.Id),
                new JsonObject
                {
                    ["text"] = result.Text,
                    ["tool_calls"] = toolCalls,
                    ["finish_reason"] = result.FinishReason,
                    ["prompt_receipt"] = WorkerV2PromptCodec.WriteReceipt(result.PromptReceipt),
                }), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SendErrorAsync(socket, sendLock, envelope.Id, "cancelled", false, nextSequence(envelope.Id), CancellationToken.None).ConfigureAwait(false);
        }
        catch (WorkerV2StepException error)
        {
            await SendErrorAsync(socket, sendLock, envelope.Id, error.Code, error.Retryable, nextSequence(envelope.Id), CancellationToken.None).ConfigureAwait(false);
        }
        catch (AgentProtocolException)
        {
            await SendErrorAsync(socket, sendLock, envelope.Id, "protocol_failure", false, nextSequence(envelope.Id), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await SendErrorAsync(socket, sendLock, envelope.Id, "generation_failed", true, nextSequence(envelope.Id), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            requestTasks.Remove(envelope.Id);
            cancellations.Remove(envelope.Id);
            cancellationSource.Dispose();
        }
    }

    private static async Task SendErrorAsync(WebSocket socket, SemaphoreSlim sendLock, string id, string code, bool retryable, long sequence, CancellationToken cancellationToken)
    {
        await SendAsync(socket, sendLock, new WorkerV2Envelope(
            WorkerV2Contracts.ProtocolVersion,
            id,
            WorkerV2Contracts.Error,
            sequence,
            new JsonObject
            {
                ["code"] = code,
                ["retryable"] = retryable,
            }), cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendAsync(WebSocket socket, SemaphoreSlim sendLock, WorkerV2Envelope envelope, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(WorkerV2EnvelopeCodec.Serialize(envelope));
        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
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
                return Encoding.UTF8.GetString(stream.ToArray());
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
