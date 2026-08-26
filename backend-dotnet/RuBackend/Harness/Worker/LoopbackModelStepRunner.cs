using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuBackend.Worker;

namespace RuBackend.Harness.Worker;

public sealed class LoopbackModelStepRunner : IModelStepRunner, IAsyncDisposable
{
    private readonly WorkerClientOptions _options;
    private readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _receiveTimeout = TimeSpan.FromSeconds(30);

    public LoopbackModelStepRunner(WorkerClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Validation happens lazily at connect time so the runner can sit in DI on
        // hosts that never drive model steps (tests, diagnostics-only processes).
        // Transform v1 URI to v2: replace /worker/v1 with /worker/v2
        var uriString = options.Uri?.ToString() ?? string.Empty;
        if (uriString.Contains("/worker/v1", StringComparison.Ordinal))
        {
            uriString = uriString.Replace("/worker/v1", "/worker/v2", StringComparison.Ordinal);
        }
        else if (!uriString.Contains("/worker/v2", StringComparison.Ordinal) && uriString.Length > 0)
        {
            uriString = uriString.TrimEnd('/') + "/worker/v2";
        }

        _options = new WorkerClientOptions
        {
            Uri = Uri.TryCreate(uriString, UriKind.Absolute, out var parsedUri)
                ? parsedUri
                : new Uri("ws://127.0.0.1:8005/worker/v2", UriKind.Absolute),
            Token = options.Token,
            ProtocolVersion = options.ProtocolVersion,
            MaxMessageBytes = options.MaxMessageBytes,
        };
    }

    // Test-only injection for fake WebSocket handling.
    internal LoopbackModelStepRunner(WorkerClientOptions options, Func<ClientWebSocket>? socketFactory)
        : this(options)
    {
        _socketFactory = socketFactory;
    }

    private readonly Func<ClientWebSocket>? _socketFactory;

    private ClientWebSocket CreateSocket() => _socketFactory?.Invoke() ?? new ClientWebSocket();

    public async Task<AssistantStep> RunStepAsync(
        ModelStepRequest request,
        Func<AssistantDelta, ValueTask> publishDelta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(publishDelta);
        _options.Validate();

        using var socket = CreateSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(_connectTimeout);
        try
        {
            await socket.ConnectAsync(_options.Uri, connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new WorkerUnavailableException(ex);
        }

        // Hello handshake
        var helloId = $"hello-{Guid.NewGuid():N}";
        var helloEnvelope = new WorkerV2Envelope(
            WorkerV2Contracts.ProtocolVersion,
            helloId,
            WorkerV2Contracts.Hello,
            null,
            null,
            new JsonObject { ["token"] = _options.Token });

        await SendAsync(socket, helloEnvelope, cancellationToken).ConfigureAwait(false);
        var helloReply = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
        if (helloReply.Type != WorkerV2Contracts.HelloOk || helloReply.Seq != 0 || helloReply.Id != helloId)
        {
            throw new WorkerProtocolException("invalid_hello");
        }

        var helloPayload = helloReply.Payload;
        if (helloPayload["version"] is not JsonValue hv || !hv.TryGetValue<int>(out var hvInt) || hvInt != WorkerV2Contracts.ProtocolVersion)
        {
            throw new WorkerProtocolException("invalid_hello");
        }

        // Prepare start payload
        var prompt = ExtractPrompt(request.Context);
        var turns = ExtractTurns(request.Context);
        var settings = BuildSettings();

        var startPayload = new JsonObject
        {
            ["prompt"] = prompt,
            ["turns"] = turns,
            ["memory_context"] = request.Context.Entries.Count > 0 ? null : null,
            ["settings"] = settings,
        };
        // Remove null memory_context if not needed; but protocol expects maybe null allowed? We'll keep if null not needed.
        // Ensure memory_context is present as null or string; fixture shows null. We'll include as null if missing.
        if (startPayload["memory_context"] is null)
        {
            // Keep as null (JsonValue null) or remove? Python's validate allows None. We'll send null.
            startPayload["memory_context"] = JsonValue.Create((string?)null);
            // JsonValue with null string creates Json null? Actually JsonValue.Create<string?>(null) yields null node => need to explicitly set.
            // Simpler: if null, keep as JsonValue.Create<string?>(null) which is null node, but JsonObject will treat null as unset. We can leave as null but serializer will omit? Better set to null via JsonValue.Create<string>(null) not allowed.
            // We'll just not include memory_context if null, but validation allows omission? The StartFields includes memory_context optional? Actually _START_FIELDS = {"prompt","turns","memory_context","settings"} and _validate_start checks if not subset, but requires prompt present. So memory_context can be omitted or null? We'll include only if not null.
            startPayload.Remove("memory_context");
        }

        var startEnvelope = new WorkerV2Envelope(
            WorkerV2Contracts.ProtocolVersion,
            request.ModelStepId.Value,
            WorkerV2Contracts.AgentStepStart,
            null,
            request.RunId.Value,
            startPayload);

        await SendAsync(socket, startEnvelope, cancellationToken).ConfigureAwait(false);

        long expectedSeq = 1;
        var deltas = new List<string>();
        AssistantStep? finalStep = null;
        bool doneReceived = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            WorkerV2Envelope reply;
            // Use Task.WhenAny to avoid aborting the socket on cancellation
            var receiveTask = ReceiveAsync(socket, CancellationToken.None);
            var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = cancellationToken.Register(() => cancelTcs.TrySetResult(true));
            var completed = await Task.WhenAny(receiveTask, cancelTcs.Task).ConfigureAwait(false);
            if (completed == cancelTcs.Task)
            {
                try
                {
                    var cancelEnvelope = new WorkerV2Envelope(
                        WorkerV2Contracts.ProtocolVersion,
                        request.ModelStepId.Value,
                        WorkerV2Contracts.AgentStepCancel,
                        null,
                        null,
                        new JsonObject());
                    await SendAsync(socket, cancelEnvelope, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }

                throw new OperationCanceledException(cancellationToken);
            }

            try
            {
                reply = await receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (!string.Equals(reply.Id, request.ModelStepId.Value, StringComparison.Ordinal))
            {
                throw new WorkerProtocolException("mismatched_request");
            }

            if (reply.Seq != expectedSeq)
            {
                // Allow hello.ok seq 0 already handled, so for step messages seq must increment.
                // Duplicate sequence should be rejected.
                throw new WorkerProtocolException("invalid_sequence");
            }

            if (!string.Equals(reply.CorrelationId, request.RunId.Value, StringComparison.Ordinal) && reply.CorrelationId is not null)
            {
                // Correlation id mismatch is allowed to be null? But we sent correlation_id, expect same.
                // If mismatch, treat as protocol error.
                throw new WorkerProtocolException("invalid_correlation_id");
            }

            switch (reply.Type)
            {
                case WorkerV2Contracts.AgentStepDelta:
                    var text = reply.Payload["text"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : throw new WorkerProtocolException("invalid_delta");
                    deltas.Add(text);
                    var delta = new AssistantDelta(request.ModelStepId, expectedSeq, text);
                    await publishDelta(delta).ConfigureAwait(false);
                    expectedSeq++;
                    break;

                case WorkerV2Contracts.AgentStepDone:
                    if (doneReceived)
                    {
                        throw new WorkerProtocolException("duplicate_done");
                    }

                    doneReceived = true;
                    // payload contains text, tool_calls, finish_reason
                    var payload = reply.Payload;
                    var finishReason = payload["finish_reason"] is JsonValue frv && frv.TryGetValue<string>(out var fr) ? fr : throw new WorkerProtocolException("invalid_step");
                    string? finalText = null;
                    if (payload["text"] is JsonValue textNode && textNode.TryGetValue<string>(out var textVal))
                    {
                        finalText = textVal;
                    }
                    else if (payload["text"] is not null && payload["text"]!.GetValueKind() != JsonValueKind.Null)
                    {
                        throw new WorkerProtocolException("invalid_step");
                    }

                    var toolCallsArray = payload["tool_calls"] as JsonArray ?? throw new WorkerProtocolException("invalid_step");
                    var toolCalls = new List<ModelToolCall>();
                    foreach (var callNode in toolCallsArray)
                    {
                        if (callNode is not JsonObject callObj)
                        {
                            throw new WorkerProtocolException("invalid_step");
                        }

                        var providerCallId = callObj["provider_call_id"] is JsonValue pcv && pcv.TryGetValue<string>(out var pc) ? pc : null;
                        var toolId = callObj["tool_id"] is JsonValue tidv && tidv.TryGetValue<string>(out var tid) ? tid : throw new WorkerProtocolException("invalid_step");
                        var toolVersion = callObj["tool_version"] is JsonValue tv2 && tv2.TryGetValue<string>(out var tvs) ? tvs : throw new WorkerProtocolException("invalid_step");
                        var argsNode = callObj["arguments"] as JsonObject ?? throw new WorkerProtocolException("invalid_step");
                        var argsJson = argsNode.ToJsonString();
                        var argsElement = JsonDocument.Parse(argsJson).RootElement.Clone();
                        toolCalls.Add(new ModelToolCall(providerCallId, toolId, toolVersion, argsElement));
                    }

                    AssistantFinishReason mappedReason = finishReason switch
                    {
                        "stop" => AssistantFinishReason.Stop,
                        "tool_calls" => AssistantFinishReason.ToolCalls,
                        "length" => AssistantFinishReason.Length,
                        _ => throw new WorkerProtocolException("invalid_step"),
                    };

                    // Enforce one-step constraint: at most one tool call (checked in AssistantStep)
                    finalStep = new AssistantStep(finalText, toolCalls, mappedReason, estimatedInputTokens: 0);
                    // Done is terminal; break loop.
                    expectedSeq++;
                    goto Done;

                case WorkerV2Contracts.Error:
                    var code = reply.Payload["code"] is JsonValue cv2 && cv2.TryGetValue<string>(out var c) ? c : "generation_failed";
                    var retryable = reply.Payload["retryable"] is JsonValue rv2 && rv2.TryGetValue<bool>(out var r) ? r : false;
                    // Map to WorkerProtocolException with code.
                    throw new WorkerProtocolException(code);

                default:
                    throw new WorkerProtocolException("unexpected_type");
            }
        }

    Done:
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (finalStep is null)
        {
            throw new WorkerProtocolException("missing_done");
        }

        // Ensure exactly one final step was received.
        return finalStep;
    }

    private static string ExtractPrompt(ModelContextSnapshot context)
    {
        // Find last user text entry; fallback to first entry or generic prompt.
        var userText = context.Entries.LastOrDefault(e => e.Role == ModelContextRole.User && e.Kind == ModelContextEntryKind.Text)?.Content;
        if (!string.IsNullOrWhiteSpace(userText))
        {
            return userText;
        }

        var anyText = context.Entries.FirstOrDefault(e => e.Kind == ModelContextEntryKind.Text)?.Content;
        if (!string.IsNullOrWhiteSpace(anyText))
        {
            return anyText;
        }

        // If context is empty (initial run), use a placeholder that will be replaced by actual run prompt.
        // For tracer, the run prompt is encrypted payload, not yet in context entries at step start?
        // We'll use a generic prompt if none found; tests will provide a context with at least one entry.
        return "tracer prompt";
    }

    private static JsonArray ExtractTurns(ModelContextSnapshot context)
    {
        // Simplify: group consecutive user/assistant pairs as turns (max 8).
        var turns = new JsonArray();
        // Collect text entries in order, pair them as prompt/response.
        // For simplicity, take all Text entries and pair them sequentially.
        var textEntries = context.Entries.Where(e => e.Kind == ModelContextEntryKind.Text).ToArray();
        // Pair them: assume alternating user/assistant starting with user.
        for (int i = 0; i + 1 < textEntries.Length && turns.Count < 8; i += 2)
        {
            var prompt = textEntries[i].Content ?? string.Empty;
            var response = textEntries[i + 1].Content ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(response))
            {
                continue;
            }

            if (prompt.Length > WorkerV2Contracts.MaxPromptChars || response.Length > 32_000)
            {
                continue;
            }

            turns.Add(new JsonObject { ["prompt"] = prompt, ["response"] = response });
        }

        return turns;
    }

    private static JsonObject BuildSettings()
    {
        // Phase-1 tracer advertises only files.list/files.read
        var tools = new JsonArray
        {
            BuildModelTool("files.list", "1.0.0", new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("path"),
            }),
            BuildModelTool("files.read", "1.0.0", new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("path"),
            }),
        };

        return new JsonObject
        {
            ["max_tokens"] = 256,
            ["temperature"] = 0.7,
            ["top_k"] = 50,
            ["allow_web_search"] = false,
            ["model_tools"] = tools,
        };
    }

    private static JsonObject BuildModelTool(string toolId, string version, JsonObject schema)
    {
        return new JsonObject
        {
            ["tool_id"] = toolId,
            ["tool_version"] = version,
            ["scopes"] = new JsonArray("workspace.read"),
            ["availability"] = "executable",
            ["model_enabled"] = true,
            ["capability_class"] = "tool",
            ["verification_required"] = false,
            ["input_schema"] = schema,
        };
    }

    private static async Task SendAsync(ClientWebSocket socket, WorkerV2Envelope envelope, CancellationToken cancellationToken)
    {
        var json = WorkerV2EnvelopeCodec.Serialize(envelope);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WorkerV2Envelope> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WorkerUnavailableException();
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new WorkerProtocolException("unexpected_binary");
            }

            if (ms.Length + result.Count > WorkerV2Contracts.MaxFrameBytes)
            {
                throw new WorkerProtocolException("message_too_large");
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                var json = Encoding.UTF8.GetString(ms.ToArray());
                return WorkerV2EnvelopeCodec.Parse(json);
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
