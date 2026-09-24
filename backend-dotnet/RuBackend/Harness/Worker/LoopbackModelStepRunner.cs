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
using RuAgent.Runtime.Adapters.Models;
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

    // Conversation blocks stay small; the untrusted runtime references keep the
    // full-fidelity tool output.
    private const int MaxConversationBlockChars = 6_000;
    private const string DurableToolContinuationPrompt = "Continue from the durable tool results.";

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
        // provider_rate_limited): each attempt gets a fresh socket and a fresh
        // single-use provider bootstrap. Deltas are forwarded to the client as
        // they arrive so the answer streams live; attempt N+1 first publishes a
        // reset frame so a client can drop the partial text of the attempt that
        // failed instead of appending the retry onto it.
        var attempt = 0;
        string? selectionFeedbackCode = null;

        // The worker restarts its per-attempt sequence at 1, so the runner keeps
        // the client-facing sequence monotonic across attempts: repeated keys
        // would otherwise make the client silently drop every retry frame as a
        // duplicate. The next attempt continues past what was already published.
        long publishedSequences = 0;
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
                catch (Exception error)
                {
                    // A config failure already names itself (missing key or model);
                    // collapsing it would send the reader after the worker instead.
                    throw new WorkerProtocolException(StableBootstrapCode(error) ?? "provider_bootstrap_failed");
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
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Attached images are part of the user's turn. Continuing without them
                // would let the model answer a question it never saw, and the user would
                // have no way to tell. Fail the step instead of silently dropping them.
                throw new WorkerProtocolException("image_payload_unavailable");
            }
        }

        var hasImages = images is { Count: > 0 };
        var (prompt, turns, modelMessages) = BuildConversation(request.Context, hasImages);
        if (selectionFeedbackCode is not null
            && BuildSelectionFeedback(selectionFeedbackCode) is { } selectionFeedback)
        {
            prompt = ApplySelectionFeedback(prompt, selectionFeedback);
        }

        var settings = BuildSettings(request.PromptContext);

        var startPayload = new JsonObject
        {
            ["prompt"] = prompt,
            ["system_prompt_context"] = WorkerV2PromptCodec.WriteContext(request.PromptContext),
            ["settings"] = settings,
        };
        if (modelMessages is not null)
        {
            startPayload["model_messages"] = WorkerV2PromptCodec.WriteModelMessages(modelMessages);
        }
        else
        {
            startPayload["turns"] = turns ?? throw new WorkerProtocolException("model_history_invalid");
        }
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

        // A retried attempt replaces the previous attempt's answer: tell the
        // client to drop the partial text it already rendered before any of this
        // attempt's text arrives. Attempt 1 has nothing to supersede.
        if (publishedSequences > 0)
        {
            await publishDelta(new AssistantDelta(
                    request.ModelStepId,
                    checked(publishedSequences + 1),
                    string.Empty,
                    isReset: true))
                .ConfigureAwait(false);
            publishedSequences++;
        }

        // The worker numbers its frames from 1 on every attempt, so re-base this
        // attempt's deltas onto the client-facing sequence already published.
        var sequenceBase = publishedSequences;
        long expectedSeq = 1;
        AssistantStep? finalStep = null;
        SystemPromptReceipt? finalReceipt = null;
        string? finalReasoningText = null;
        bool doneReceived = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            // A step the Worker accepts and then never answers would park the run
            // forever, so the wait is bounded. The bound is per frame, not per step:
            // deltas arrive continuously during a generation, and a legitimate long
            // answer must not be cut off by a total-step deadline.
            //
            // The timer drives the wait only. It is deliberately NOT linked to this
            // method's token: the receive below must stay pending so that cancellation
            // can still write agent.step.cancel on a live socket. Cancelling the receive
            // aborts the connection and the Worker never learns the step was cancelled.
            using var receiveTimer = new CancellationTokenSource(_receiveTimeout);
            WorkerV2Envelope reply;
            var receiveTask = ReceiveAsync(socket, CancellationToken.None);
            var waitOver = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancelReg = cancellationToken.Register(() => waitOver.TrySetResult(true));
            using var timerReg = receiveTimer.Token.Register(() => waitOver.TrySetResult(true));
            var completed = await Task.WhenAny(receiveTask, waitOver.Task).ConfigureAwait(false);
            if (completed == waitOver.Task)
            {
                // The receive stays pending on purpose: cancelling it would
                // abort the shared socket. Observe its eventual fault here,
                // because the socket is disposed as this method unwinds and an
                // unobserved faulted receive task surfaces as an unobserved
                // exception later.
                _ = receiveTask.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

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

                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new WorkerUnavailableException(new TimeoutException(
                    $"worker_silent:{_receiveTimeout.TotalSeconds:0}s"));
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

            if (reply.Type is WorkerV2Contracts.AgentStepDelta or WorkerV2Contracts.AgentStepDone
                && !string.Equals(reply.CorrelationId, request.RunId.Value, StringComparison.Ordinal))
            {
                // Every step frame must carry the request's correlation id
                // exactly; error frames are matched by request id instead.
                throw new WorkerProtocolException("invalid_correlation_id");
            }

            switch (reply.Type)
            {
                case WorkerV2Contracts.AgentStepDelta:
                    var text = reply.Payload["text"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : throw new WorkerProtocolException("invalid_delta");
                    var delta = new AssistantDelta(
                        request.ModelStepId,
                        checked(sequenceBase + expectedSeq),
                        text);
                    expectedSeq++;
                    publishedSequences++;
                    // Forward the delta now: buffering until Done made the whole
                    // answer appear at once, so the turn looked frozen while the
                    // model was actually writing.
                    await publishDelta(delta).ConfigureAwait(false);
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

                    string? reasoningText = null;
                    if (payload["reasoning_text"] is JsonValue reasoningNode
                        && reasoningNode.TryGetValue<string>(out var reasoningVal)
                        && !string.IsNullOrWhiteSpace(reasoningVal))
                    {
                        reasoningText = reasoningVal.Length <= 8_000
                            ? reasoningVal
                            : reasoningVal[^8_000..];
                    }
                    else if (payload["reasoning_text"] is not null
                        && payload["reasoning_text"]!.GetValueKind() != JsonValueKind.Null)
                    {
                        throw new WorkerProtocolException("invalid_step");
                    }
                    finalReasoningText = reasoningText;

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
                        try
                        {
                            toolCalls.Add(new ModelToolCall(providerCallId!, toolId, toolVersion, argsElement));
                        }
                        catch (ArgumentException)
                        {
                            throw new WorkerProtocolException("invalid_step");
                        }
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
                    try
                    {
                        finalStep = new AssistantStep(finalText, toolCalls, mappedReason, estimatedInputTokens: 0, usage);
                    }
                    catch (ArgumentException)
                    {
                        throw new WorkerProtocolException("invalid_step");
                    }

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

        // Deltas were already forwarded live as they arrived; nothing to replay.

        // Ensure exactly one final step was received.
        return new ModelStepResult(
            finalStep,
            finalReceipt ?? throw new WorkerProtocolException("missing_prompt_receipt"),
            finalStep.Usage,
            finalReasoningText);
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
            // A model-formatting rejection is handed back to the model on the
            // next attempt instead of failing the run closed.
            selectionFeedbackCode = error.Code;
            attempt++;
        }
        catch (WorkerUnavailableException) when (
            attempt + 1 < MaxModelStepAttempts
            && !cancellationToken.IsCancellationRequested)
        {
            // The worker is a separate process, so a moment when it is not listening says
            // nothing about the request. This used to escape the retry budget and end the
            // turn as "worker_unavailable", which turned a slow or restarting worker into a
            // failed conversation.
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

    /// <summary>
    /// Keeps a provider selection failure's own bounded code when it already
    /// satisfies the terminal-code shape (1..64 chars, [a-z] first, [a-z0-9_.]
    /// after); anything else still collapses to the generic bootstrap failure.
    /// </summary>
    private static string? StableBootstrapCode(Exception error)
    {
        if (error is not Providers.ProviderCatalogException catalog || string.IsNullOrWhiteSpace(catalog.Code))
        {
            return null;
        }

        var code = catalog.Code.Trim();
        if (code.Length is < 1 or > 64 || code[0] < 'a' || code[0] > 'z')
        {
            return null;
        }

        foreach (var ch in code)
        {
            var ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '.' || ch == '_';
            if (!ok)
            {
                return null;
            }
        }

        return code;
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

    private static (string Prompt, JsonArray? Turns, IReadOnlyList<ModelTurn>? Messages) BuildConversation(
        ModelContextSnapshot context,
        bool hasImages)
    {
        try
        {
            var (prompt, messages) = BuildTypedConversation(context, hasImages);
            return (prompt, null, messages);
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw new WorkerProtocolException("model_history_invalid");
        }
    }

    private static (string Prompt, IReadOnlyList<ModelTurn> Messages) BuildTypedConversation(
        ModelContextSnapshot context,
        bool hasImages)
    {
        var units = new List<List<ModelTurn>>();
        var pendingUserBlocks = new List<string>();
        var pendingPromptBlocks = new List<string>();
        var pendingCalls = new Queue<ModelFunctionCall>();
        var providerCallIds = new HashSet<string>(StringComparer.Ordinal);
        var providerIdsByToolCall = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in context.Entries)
        {
            if (entry.Kind == ModelContextEntryKind.Text && entry.Role == ModelContextRole.System)
            {
                EnsureNoPendingCalls(pendingCalls);
                if (!string.IsNullOrWhiteSpace(entry.Content))
                {
                    AddPendingUserBlocks(units, pendingUserBlocks);
                    units.Add(new List<ModelTurn> { new("system", entry.Content) });
                }
                continue;
            }

            if (entry.Kind == ModelContextEntryKind.Text && entry.Role == ModelContextRole.User)
            {
                EnsureNoPendingCalls(pendingCalls);
                if (!string.IsNullOrWhiteSpace(entry.Content))
                {
                    pendingUserBlocks.Add(entry.Content);
                    pendingPromptBlocks.Add(entry.Content);
                }
                continue;
            }

            if (entry.Kind == ModelContextEntryKind.Summary && !string.IsNullOrWhiteSpace(entry.Content))
            {
                EnsureNoPendingCalls(pendingCalls);
                var summary = ClampConversationBlock(
                    $"compacted history summary:\n{entry.Content}",
                    MaxConversationBlockChars);
                AddPendingUserBlocks(units, pendingUserBlocks);
                units.Add(new List<ModelTurn> { new("user", summary) });
                pendingPromptBlocks.Add(summary);
                continue;
            }

            if (entry.Kind == ModelContextEntryKind.AssistantStep && entry.AssistantStep is { } step)
            {
                EnsureNoPendingCalls(pendingCalls);
                if (step.ToolCalls.Count > 0 || !string.IsNullOrWhiteSpace(step.Text))
                {
                    pendingPromptBlocks.Clear();
                }
                AddAssistantMessages(units, pendingUserBlocks, step, providerCallIds, pendingCalls);
                continue;
            }

            if (entry.Kind == ModelContextEntryKind.ToolResult)
            {
                AddToolResultMessage(units, entry, pendingCalls, providerIdsByToolCall);
            }
        }

        if (pendingCalls.Count > 0)
        {
            throw new WorkerProtocolException("model_history_invalid");
        }

        var messages = FitModelMessagesToBudget(units);
        return (ResolveTypedPrompt(messages, pendingPromptBlocks, hasImages), messages);
    }

    internal static (string Prompt, JsonArray? Turns, IReadOnlyList<ModelTurn>? Messages) BuildConversationForTests(
        ModelContextSnapshot context,
        bool hasImages = false) => BuildConversation(context, hasImages);

    private static void EnsureNoPendingCalls(Queue<ModelFunctionCall> pendingCalls)
    {
        if (pendingCalls.Count > 0)
        {
            throw new WorkerProtocolException("model_history_invalid");
        }
    }

    private static void AddAssistantMessages(
        List<List<ModelTurn>> units,
        List<string> pendingUserBlocks,
        AssistantStep step,
        HashSet<string> providerCallIds,
        Queue<ModelFunctionCall> pendingCalls)
    {
        if (step.ToolCalls.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(step.Text))
            {
                return;
            }

            AddPendingUserBlocks(units, pendingUserBlocks);
            units.Add(new List<ModelTurn> { new("assistant", step.Text) });
            return;
        }

        AddPendingUserBlocks(units, pendingUserBlocks);
        var calls = new List<ModelFunctionCall>(step.ToolCalls.Count);
        foreach (var call in step.ToolCalls)
        {
            var providerCallId = call.ProviderCallId
                ?? throw new WorkerProtocolException("model_history_invalid");
            if (!providerCallIds.Add(providerCallId))
            {
                throw new WorkerProtocolException("model_history_invalid");
            }

            var functionCall = new ModelFunctionCall(providerCallId, call.ToolId, call.Arguments);
            calls.Add(functionCall);
            pendingCalls.Enqueue(functionCall);
        }

        units.Add(new List<ModelTurn>
        {
            new("assistant", step.Text ?? string.Empty, calls),
        });
    }

    private static void AddPendingUserBlocks(
        List<List<ModelTurn>> units,
        List<string> pendingUserBlocks)
    {
        foreach (var block in pendingUserBlocks)
        {
            units.Add(new List<ModelTurn> { new("user", block) });
        }

        pendingUserBlocks.Clear();
    }

    private static void AddToolResultMessage(
        List<List<ModelTurn>> units,
        ModelContextEntry entry,
        Queue<ModelFunctionCall> pendingCalls,
        Dictionary<string, string> providerIdsByToolCall)
    {
        var receipt = entry.ToolReceipt
            ?? throw new WorkerProtocolException("model_history_invalid");
        if (pendingCalls.Count == 0 || units.Count == 0)
        {
            throw new WorkerProtocolException("model_history_invalid");
        }

        var providerCallId = receipt.ProviderCallId
            ?? throw new WorkerProtocolException("model_history_invalid");
        var call = pendingCalls.Peek();
        if (!string.Equals(call.Id, providerCallId, StringComparison.Ordinal)
            || !providerIdsByToolCall.TryAdd(receipt.ToolCallId.Value, providerCallId))
        {
            throw new WorkerProtocolException("model_history_invalid");
        }

        pendingCalls.Dequeue();
        units[^1].Add(new ModelTurn(
            "tool",
            ClampConversationBlock(entry.Content ?? string.Empty, MaxConversationBlockChars),
            toolCallId: call.Id,
            toolResultCode: receipt.Code));
    }

    private static string ResolveTypedPrompt(
        IReadOnlyList<ModelTurn> messages,
        List<string> pendingPromptBlocks,
        bool hasImages)
    {
        if (pendingPromptBlocks.Count > 0)
        {
            return ClampConversationBlock(
                string.Join("\n\n", pendingPromptBlocks),
                WorkerV2Contracts.MaxPromptChars);
        }

        if (messages.Count > 0 && messages[^1].Role == "tool")
        {
            return DurableToolContinuationPrompt;
        }

        var fallback = messages.LastOrDefault(message => message.Role == "user")?.Content;
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback;
        }

        return hasImages
            ? string.Empty
            : throw new WorkerProtocolException("missing_user_prompt");
    }

    private static IReadOnlyList<ModelTurn> FitModelMessagesToBudget(
        List<List<ModelTurn>> units)
    {
        var selected = new List<List<ModelTurn>>();
        for (var index = units.Count - 1; index >= 0; index--)
        {
            var unit = units[index];
            var candidateMessages = selected
                .SelectMany(selectedUnit => selectedUnit)
                .Concat(unit)
                .ToArray();
            var candidateCount = candidateMessages.Length;
            var candidateBytes = ModelRequest.MeasureModelMessagesBytes(candidateMessages);
            if (candidateCount > WorkerV2Contracts.MaxModelMessages
                || candidateBytes > WorkerV2Contracts.MaxModelMessagesBytes)
            {
                if (selected.Count == 0)
                {
                    throw new WorkerProtocolException("model_history_too_large");
                }

                break;
            }

            selected.Add(unit);
        }

        selected.Reverse();
        return selected.SelectMany(unit => unit).ToArray();
    }

    /// <summary>
    /// Model-facing feedback for the bounded retry after a rejected tool
    /// selection: the coding-agent behaviour of handing a formatting mistake
    /// back to the model instead of failing the run closed. Unknown codes keep
    /// the plain retry (provider/stream anomalies are not model mistakes).
    /// </summary>
    internal static string? BuildSelectionFeedback(string errorCode) => errorCode switch
    {
        "model_tool_call_invalid" =>
            "Your previous tool call was rejected: its arguments were incomplete or invalid. Re-issue the call with complete JSON arguments.",
        "model_tool_call_unavailable" =>
            "Your previous tool call was rejected: that tool is not advertised. Re-issue the call for an advertised tool.",
        "invalid_selection" =>
            "Your previous reply was rejected: the tool selection was malformed. Re-issue one valid JSON tool selection, or answer in plain text.",
        _ => null,
    };

    internal static string ApplySelectionFeedbackForTests(string prompt, string errorCode)
    {
        var feedback = BuildSelectionFeedback(errorCode);
        return feedback is null ? prompt : ApplySelectionFeedback(prompt, feedback);
    }

    private static string ApplySelectionFeedback(string prompt, string feedback)
    {
        // The feedback note is the part the model must see, so the prompt body
        // is clamped first and the note is always appended inside the budget.
        var note = $"\n\n[harness] {feedback}";
        var budget = Math.Max(1, WorkerV2Contracts.MaxPromptChars - note.Length);
        var body = prompt.Length <= budget ? prompt : ClampConversationBlock(prompt, budget);
        return body + note;
    }

    /// <summary>
    /// Keeps one conversation block inside the worker prompt budget while the
    /// untrusted runtime references retain the full-fidelity tool output.
    /// </summary>
    private static string ClampConversationBlock(string block, int maxChars)
    {
        if (block.Length <= maxChars)
        {
            return block;
        }

        const int MarkerBudget = 160;
        var prefix = Math.Max(1, maxChars - MarkerBudget);
        var omitted = block.Length - prefix;
        return block[..prefix]
            + $"\n…(truncated: {omitted} of {block.Length} characters omitted; full output stays in the runtime references)";
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
                || !string.Equals(AgentToolDefinitionCatalog.CanonicalSchemaJson(definition.InputSchema), capability.InputSchemaJson, StringComparison.Ordinal))
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
