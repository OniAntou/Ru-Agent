using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Worker;
using RuBackend.Worker;

namespace RuBackend.Harness.Worker;

public sealed class LoopbackModelStepRunner : IModelStepRunner, IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly AgentWorkerClientOptions _options;
    private readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _receiveTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupConnectWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StartupConnectBackoff = TimeSpan.FromMilliseconds(400);
    private const int MaxModelStepAttempts = 3;
    private static readonly TimeSpan[] ModelStepRetryBackoffs = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    // Tests shrink this window; production retries for the full window.
    internal TimeSpan StartupConnectWindowOverride { get; set; }

    // Tests shrink the backoff; production waits out transient provider limits.
    internal TimeSpan[] StepRetryBackoffsOverride { get; set; } = ModelStepRetryBackoffs;

    public LoopbackModelStepRunner(AgentWorkerClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Validation happens lazily at connect time so the runner can sit in DI on
        // hosts that never drive model steps (tests, diagnostics-only processes).
        _options = options;
    }

    public Func<ModelStepRequest, CancellationToken, Task<string>>? CreateProviderBootstrapAsync { get; init; }
    public Func<RunId, CancellationToken, Task<IReadOnlyList<string>>>? LoadRunImagesAsync { get; init; }

    // Test-only injection for fake WebSocket handling.
    internal LoopbackModelStepRunner(AgentWorkerClientOptions options, Func<ClientWebSocket>? socketFactory)
        : this(options)
    {
        _socketFactory = socketFactory;
    }

    private readonly Func<ClientWebSocket>? _socketFactory;

    private ClientWebSocket CreateSocket() => _socketFactory?.Invoke() ?? new ClientWebSocket();

    public async Task<ModelStepResult> RunStepAsync(
        ModelStepRequest request,
        Func<AssistantDelta, ValueTask> publishDelta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(publishDelta);
        _options.Validate();

        // Bounded retry for Worker-reported transient failures (e.g.
        // provider_rate_limited): each attempt gets a fresh socket and a
        // fresh single-use provider bootstrap, and deltas stay buffered
        // until Done, so retries never duplicate visible output.
        var attempt = 0;
        while (true)
        {
        string? providerBootstrapPath = null;
        try
        {
            if (CreateProviderBootstrapAsync is not null)
            {
                try
                {
                    providerBootstrapPath = await CreateProviderBootstrapAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    throw new WorkerProtocolException("provider_bootstrap_failed");
                }

                if (string.IsNullOrWhiteSpace(providerBootstrapPath))
                {
                    throw new WorkerProtocolException("provider_bootstrap_failed");
                }
            }

            // The managed Worker can still be binding its loopback port when a
            // run starts right after Host startup; retry the connect window in
            // a bounded loop instead of failing the whole run on the first
            // refused socket.
            var startupDeadline = Environment.TickCount64 + (long)(
                StartupConnectWindowOverride == default
                    ? StartupConnectWindow
                    : StartupConnectWindowOverride).TotalMilliseconds;
            while (true)
            {
                using var probe = CreateSocket();
                probe.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeCts.CancelAfter(_connectTimeout);
                try
                {
                    await probe.ConnectAsync(_options.Uri, probeCts.Token).ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    if (Environment.TickCount64 >= startupDeadline)
                    {
                        // The real connect below throws the exact failure.
                        break;
                    }

                    await Task.Delay(StartupConnectBackoff, cancellationToken).ConfigureAwait(false);
                }
            }

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
        IReadOnlyList<string>? images = null;
        if (LoadRunImagesAsync is not null)
        {
            try
            {
                images = await LoadRunImagesAsync(request.RunId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Fallback gracefully if image loading fails
            }
        }

        var hasImages = images is { Count: > 0 };
        var prompt = ExtractPrompt(request.Context, hasImages);
        var turns = ExtractTurns(request.Context);
        var settings = BuildSettings(request.PromptContext);

        var startPayload = new JsonObject
        {
            ["prompt"] = prompt,
            ["turns"] = turns,
            ["system_prompt_context"] = WorkerV2PromptCodec.WriteContext(request.PromptContext),
            ["settings"] = settings,
        };
        if (hasImages)
        {
            var imagesArr = new JsonArray();
            foreach (var img in images!)
            {
                imagesArr.Add(img);
            }
            startPayload["images"] = imagesArr;
        }
        if (providerBootstrapPath is not null)
        {
            startPayload["provider_bootstrap_path"] = providerBootstrapPath;
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
        AssistantStep? finalStep = null;
        SystemPromptReceipt? finalReceipt = null;
        var bufferedDeltas = new List<AssistantDelta>();
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
                    var delta = new AssistantDelta(request.ModelStepId, expectedSeq, text);
                    bufferedDeltas.Add(delta);
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
                        using var argsDocument = JsonDocument.Parse(argsJson);
                        var argsElement = argsDocument.RootElement.Clone();
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
                    ModelUsage? usage = null;
                    if (payload["usage"] is JsonNode usageNode)
                    {
                        try
                        {
                            using var usageDocument = JsonDocument.Parse(usageNode.ToJsonString());
                            usage = ModelUsage.Read(usageDocument.RootElement);
                        }
                        catch (Exception exception) when (exception is
                            JsonException
                            or InvalidDataException
                            or ArgumentException)
                        {
                            throw new WorkerProtocolException("invalid_step");
                        }
                    }
                    finalStep = new AssistantStep(finalText, toolCalls, mappedReason, estimatedInputTokens: 0, usage);
                    try
                    {
                        finalReceipt = WorkerV2PromptCodec.ReadReceipt(
                            payload["prompt_receipt"] as JsonObject
                            ?? throw new WorkerProtocolException("invalid_step"),
                            request.PromptContext);
                    }
                    catch (AgentProtocolException error)
                    {
                        throw new WorkerProtocolException(error.Code);
                    }
                    // Done is terminal; break loop.
                    expectedSeq++;
                    goto Done;

                case WorkerV2Contracts.Error:
                    var code = reply.Payload["code"] is JsonValue cv2 && cv2.TryGetValue<string>(out var c) ? c : "generation_failed";
                    var retryable = reply.Payload["retryable"] is JsonValue rv2 && rv2.TryGetValue<bool>(out var r) ? r : false;
                    // Preserve the Worker's retry signal; the step driver retries
                    // bounded transient failures instead of failing the run.
                    throw new WorkerProtocolException(code, retryable);

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

        foreach (var delta in bufferedDeltas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await publishDelta(delta).ConfigureAwait(false);
        }

        // Ensure exactly one final step was received.
        return new ModelStepResult(
            finalStep,
            finalReceipt ?? throw new WorkerProtocolException("missing_prompt_receipt"),
            finalStep.Usage);
        }
        catch (WorkerProtocolException error) when (
            error.Retryable
            && attempt + 1 < MaxModelStepAttempts
            && !cancellationToken.IsCancellationRequested)
        {
            var backoffs = StepRetryBackoffsOverride.Length == MaxModelStepAttempts - 1
                ? StepRetryBackoffsOverride
                : ModelStepRetryBackoffs;
            await Task.Delay(backoffs[attempt], cancellationToken).ConfigureAwait(false);
            attempt++;
        }
        finally
        {
            if (providerBootstrapPath is not null)
            {
                DeleteOwnedProviderBootstrap(providerBootstrapPath);
            }
        }
        }
    }

    private static void DeleteOwnedProviderBootstrap(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(root))
            {
                ProtectedProviderBootstrap.Delete(root, fullPath);
            }
        }
        catch
        {
            // Cleanup must not replace the step's actual protocol or cancellation error.
        }
    }

    private static string ExtractPrompt(ModelContextSnapshot context, bool hasImages = false)
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

        if (hasImages)
        {
            return string.Empty;
        }

        // Context must contain at least one text entry for the model step.
        // If none is found the context assembly pipeline failed to include the
        // user prompt — surface a clear error rather than sending a meaningless
        // placeholder that confuses the model.
        throw new WorkerProtocolException("missing_user_prompt");
    }

    private static JsonArray ExtractTurns(ModelContextSnapshot context)
    {
        var allPairs = new List<JsonObject>();
        string? pendingUser = null;
        foreach (var entry in context.Entries)
        {
            if (entry.Role == ModelContextRole.User
                && entry.Kind == ModelContextEntryKind.Text
                && !string.IsNullOrWhiteSpace(entry.Content))
            {
                pendingUser = entry.Content;
                continue;
            }

            if (pendingUser is null
                || entry.Role != ModelContextRole.Assistant
                || entry.Kind != ModelContextEntryKind.AssistantStep
                || string.IsNullOrWhiteSpace(entry.AssistantStep?.Text))
            {
                continue;
            }

            var response = entry.AssistantStep.Text;
            if (pendingUser.Length <= WorkerV2Contracts.MaxPromptChars && response.Length <= 32_000)
            {
                allPairs.Add(new JsonObject { ["prompt"] = pendingUser, ["response"] = response });
            }

            pendingUser = null;
        }

        var turns = new JsonArray();
        var recentPairs = allPairs.TakeLast(8);
        foreach (var pair in recentPairs)
        {
            turns.Add(pair);
        }

        return turns;
    }

    private static JsonObject BuildSettings(SystemPromptContext context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tools = new JsonArray();
        foreach (var capability in context.Capabilities)
        {
            var definition = AgentToolDefinitionCatalog.Find(capability.Id, capability.Version);
            var key = $"{capability.Id}@{capability.Version}";
            if (definition is null
                || !seen.Add(key)
                || !string.Equals(definition.Scope, capability.Scope, StringComparison.Ordinal)
                || !string.Equals(AgentToolDefinitionCatalog.ComputeSchemaDigest(definition.InputSchema), capability.InputSchemaDigest, StringComparison.Ordinal)
                || !string.Equals(definition.InputSchema.ToJsonString(CompactJson), capability.InputSchemaJson, StringComparison.Ordinal))
            {
                throw new AgentProtocolException("tool_contract_mismatch");
            }

            tools.Add(BuildModelTool(definition.ToolId, definition.ToolVersion, definition.Scope, definition.InputSchema));
        }

        return new JsonObject
        {
            ["model_tools"] = tools,
        };
    }

    internal static JsonObject BuildSettingsForTests(SystemPromptContext context) => BuildSettings(context);

    private static JsonObject BuildModelTool(
        string toolId,
        string version,
        string scope,
        JsonObject schema)
    {
        return new JsonObject
        {
            ["tool_id"] = toolId,
            ["tool_version"] = version,
            ["scopes"] = new JsonArray(scope),
            ["availability"] = "executable",
            ["model_enabled"] = true,
            ["capability_class"] = "tool",
            ["verification_required"] = false,
            ["input_schema"] = schema,
        };
    }

    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

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
                string json;
                try
                {
                    json = StrictUtf8.GetString(ms.GetBuffer(), 0, checked((int)ms.Length));
                }
                catch (DecoderFallbackException)
                {
                    throw new WorkerProtocolException("invalid_utf8");
                }

                try
                {
                    return WorkerV2EnvelopeCodec.Parse(json);
                }
                catch (AgentProtocolException error)
                {
                    throw new WorkerProtocolException(error.Code);
                }
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
