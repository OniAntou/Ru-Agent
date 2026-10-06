using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Worker;
using RuBackend.Harness.Prompting;

namespace RuBackend.Harness.Worker;

/// <summary>
/// Runs one model step inside the Host process against the typed
/// <see cref="WorkerV2StepService"/>. This is the loopback runner's logic with
/// the socket, hello/probe/cancel frames and the provider-bootstrap handoff file
/// replaced by a typed provider snapshot: conversation build, delta sequencing,
/// bounded retry/backoff and the durable failure mapping are unchanged.
/// </summary>
public sealed class InProcessModelStepRunner : IProviderBackedModelStepRunner, IModelStepRequestPreparer
{
    private const int MaxModelStepAttempts = 3;
    private static readonly TimeSpan[] ModelStepRetryBackoffs = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    // Conversation blocks stay small; untrusted runtime references carry bounded
    // source output and explicit cursors when more can be fetched.
    private const int MaxConversationBlockChars = 12_000;
    private const string DurableToolContinuationPrompt = "Continue from the durable tool results.";
    private const string ConversationTruncationNote =
        "\n…(truncated at conversation block limit; inspect the untrusted tool reference and continue at projection_next_offset when present)";

    private readonly WorkerV2StepService _stepService;

    public InProcessModelStepRunner(WorkerV2StepService stepService)
    {
        ArgumentNullException.ThrowIfNull(stepService);
        _stepService = stepService;
    }

    /// <summary>
    /// Resolves the per-run provider snapshot. Null on hosts that never drive
    /// model steps (the historical deterministic harness); a step on such a host
    /// fails closed with <c>provider_bootstrap_failed</c>.
    /// </summary>
    public Func<RunId, CancellationToken, Task<ProviderBootstrapData>>? CreateProviderBootstrapAsync { get; init; }

    /// <summary>Loads the run's attached images from the payload store as data URLs.</summary>
    public Func<RunId, CancellationToken, Task<IReadOnlyList<string>>>? LoadRunImagesAsync { get; init; }

    public Func<RunId, ProviderBootstrapData, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<ProviderContinuation>>>? LoadProviderContinuationsAsync { get; init; }

    // Tests shrink the backoff; production waits out transient provider limits.
    internal TimeSpan[] StepRetryBackoffsOverride { get; set; } = ModelStepRetryBackoffs;

    internal static IReadOnlyList<TimeSpan> DefaultStepRetryBackoffs => ModelStepRetryBackoffs;

    public async Task<ModelStepResult> RunStepAsync(
        ModelStepRequest request,
        Func<AssistantDelta, ValueTask> publishDelta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(publishDelta);

        // Bounded retry for provider-reported transient failures (e.g.
        // provider_rate_limited): each attempt gets a fresh provider snapshot.
        // Deltas are forwarded to the client as they arrive so the answer
        // streams live; attempt N+1 first publishes a reset frame so a client
        // can drop the partial text of the attempt that failed instead of
        // appending the retry onto it.
        var attempt = 0;
        string? selectionFeedbackCode = null;

        // Each attempt numbers its deltas from 1, so the runner keeps the
        // client-facing sequence monotonic across attempts: repeated keys would
        // otherwise make the client silently drop every retry frame as a
        // duplicate. The next attempt continues past what was already published.
        long publishedSequences = 0;

        while (true)
        {
            try
            {
                var prepared = attempt == 0 ? request.Preparation as InProcessPreparedStep : null;
                if (attempt == 0 && request.Preparation is not null && prepared is null)
                    throw new WorkerProtocolException("invalid_request", retryable: false);
                var workerPrepared = prepared?.WorkerRequest
                    ?? _stepService.PrepareRequest(
                        await BuildGenerationRequestAsync(request, selectionFeedbackCode, cancellationToken)
                            .ConfigureAwait(false), request.ModelStepId.Value, requireExactProjection: false);
                if (workerPrepared.EstimatedInputTokens >= 0
                    && workerPrepared.EstimatedInputTokens
                        > workerPrepared.MaxPromptTokens - RequestSafetyMarginTokens)
                    throw new WorkerV2StepException("context_limit_reached", retryable: false);
                // A retried attempt replaces the previous attempt's answer: tell the
                // client to drop the partial text it already rendered before any of this
                // attempt's text arrives. Attempt 1 has nothing to supersede.
                if (publishedSequences > 0)
                {
                    var resetSequence = checked(++publishedSequences);
                    await publishDelta(new AssistantDelta(
                            request.ModelStepId,
                            resetSequence,
                            string.Empty,
                            isReset: true))
                        .ConfigureAwait(false);
                }

                // The service numbers this attempt's deltas from 1; re-base them
                // onto the client-facing sequence already published.
                var result = await _stepService.RunPreparedAsync(
                        workerPrepared,
                        async text =>
                        {
                            var sequence = checked(++publishedSequences);
                            await publishDelta(new AssistantDelta(request.ModelStepId, sequence, text))
                                .ConfigureAwait(false);
                        },
                        cancellationToken,
                        request.ModelStepId.Value)
                    .ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                var finalStep = new AssistantStep(
                    result.Text,
                    result.ToolCalls,
                    MapFinishReason(result.FinishReason),
                    estimatedInputTokens: 0,
                    result.Usage);
                return new ModelStepResult(finalStep, result.PromptReceipt, finalStep.Usage, result.ReasoningText, result.ProviderContinuation);
            }
            catch (WorkerV2StepException error) when (
                error.Retryable
                && attempt + 1 < MaxModelStepAttempts
                && !cancellationToken.IsCancellationRequested)
            {
                // One line per retryable rejection keeps a run whose retried
                // attempt also fails diagnosable, like the worker transport
                // reported every error frame before the retry.
                Console.Error.WriteLine(
                    $"ru.host step_rejected code={error.Code} retryable={error.Retryable}");
                var backoffs = StepRetryBackoffsOverride.Length == MaxModelStepAttempts - 1
                    ? StepRetryBackoffsOverride
                    : ModelStepRetryBackoffs;
                await Task.Delay(backoffs[attempt], cancellationToken).ConfigureAwait(false);
                // A model-formatting rejection is handed back to the model on the
                // next attempt instead of failing the run closed.
                selectionFeedbackCode = error.Code;
                attempt++;
            }
            catch (WorkerV2StepException error)
            {
                Console.Error.WriteLine(
                    $"ru.host step_rejected code={error.Code} retryable={error.Retryable}");
                // The step driver records one durable code, and the provider
                // code must survive the wrap: the stable carrier keeps
                // exhausted transients distinguishable instead of collapsing
                // them into model_failed.
                Console.Error.WriteLine(
                    $"ru.host step_failed code={error.Code} attempt={attempt} retryable={error.Retryable}");
                throw new WorkerProtocolException(error.Code, error.Retryable, error);
            }
            catch (WorkerProtocolException error)
            {
                // A protocol rejection carried a bounded code already; one log
                // line is the difference between a diagnosable failure and a guess.
                Console.Error.WriteLine(
                    $"ru.host step_failed code={error.Code} attempt={attempt} retryable=false");
                throw;
            }
        }
    }

    private const long RequestSafetyMarginTokens = 64;

    public async Task<IPreparedModelStep> PrepareAsync(
        ModelStepRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var generationRequest = await BuildGenerationRequestAsync(request, null, cancellationToken)
            .ConfigureAwait(false);
        var workerRequest = _stepService.PrepareRequest(
            generationRequest, request.ModelStepId.Value, requireExactProjection: true);
        if (workerRequest.EstimatedInputTokens < 0 || workerRequest.MaxPromptTokens <= 0)
            throw new WorkerV2StepException("provider_request_projection_unavailable", retryable: false);
        return new InProcessPreparedStep(workerRequest);
    }

    private async Task<ModelGenerationRequest> BuildGenerationRequestAsync(
        ModelStepRequest request,
        string? selectionFeedbackCode,
        CancellationToken cancellationToken)
    {
        var bootstrap = await LoadProviderBootstrapAsync(request.RunId, cancellationToken).ConfigureAwait(false);
        var images = await LoadImagesAsync(request.RunId, cancellationToken).ConfigureAwait(false);
        var (prompt, _, modelMessages) = BuildConversation(request.Context, images.Count > 0);
        var history = await BuildNativeHistoryAsync(request, bootstrap, modelMessages!, cancellationToken)
            .ConfigureAwait(false);
        if (selectionFeedbackCode is not null
            && BuildSelectionFeedback(selectionFeedbackCode) is { } selectionFeedback)
            prompt = ApplySelectionFeedback(prompt, selectionFeedback);
        var settings = BuildSettings(request.PromptContext);
        try
        {
            return new ModelGenerationRequest(prompt, modelMessages!, request.PromptContext,
                new ModelGenerationSettings(settings["model_tools"] as JsonArray), bootstrap,
                ParseRunImages(images), history.Responses, history.Anthropic);
        }
        catch (ArgumentException error)
        {
            throw new WorkerProtocolException("invalid_request", retryable: false, error);
        }
    }

    private async Task<NativeHistory> BuildNativeHistoryAsync(
        ModelStepRequest request,
        ProviderBootstrapData bootstrap,
        IReadOnlyList<ModelTurn> modelMessages,
        CancellationToken cancellationToken)
    {
        var empty = new NativeHistory(Array.Empty<JsonObject>(), Array.Empty<JsonObject>());
        if (bootstrap.ToolCallingMode != ProviderToolCallingMode.Native
            || bootstrap.Protocol is not (ProviderProtocols.OpenAiResponses or ProviderProtocols.AnthropicMessages))
            return empty;
        var continuations = LoadProviderContinuationsAsync is null
            ? Array.Empty<ProviderContinuation>()
            : await LoadProviderContinuationsAsync(request.RunId, bootstrap,
                RetainedAssistantEventIds(request.Context, modelMessages), cancellationToken).ConfigureAwait(false);
        return bootstrap.Protocol == ProviderProtocols.OpenAiResponses
            ? new NativeHistory(OpenAiResponsesHistoryProjector.Project(modelMessages, continuations), empty.Anthropic)
            : new NativeHistory(empty.Responses,
                AnthropicMessagesHistoryProjector.Project(modelMessages, continuations, bootstrap.Model));
    }

    private async Task<ProviderBootstrapData> LoadProviderBootstrapAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        if (CreateProviderBootstrapAsync is null)
        {
            throw new WorkerProtocolException("provider_bootstrap_failed");
        }

        try
        {
            return await CreateProviderBootstrapAsync(runId, cancellationToken).ConfigureAwait(false)
                ?? throw new WorkerProtocolException("provider_bootstrap_failed");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (Exception error)
        {
            // A config failure already names itself (missing key or model);
            // collapsing it would send the reader after the provider instead.
            throw new WorkerProtocolException(
                StableBootstrapCode(error) ?? "provider_bootstrap_failed", retryable: false, error);
        }
    }

    private async Task<IReadOnlyList<string>> LoadImagesAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        if (LoadRunImagesAsync is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            return await LoadRunImagesAsync(runId, cancellationToken).ConfigureAwait(false)
                ?? Array.Empty<string>();
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
            throw new WorkerProtocolException("image_payload_unavailable", retryable: false, error);
        }
    }

    private static IReadOnlyList<ModelImage> ParseRunImages(IReadOnlyList<string> dataUrls)
    {
        var images = new List<ModelImage>(dataUrls.Count);
        foreach (var dataUrl in dataUrls)
        {
            // One data URL per stored image. The payload store only holds entries
            // validated when the run was created, so an entry that no longer
            // carries a ";base64," marker is skipped rather than failing the
            // step; an entry the image contract rejects still fails the step as
            // an invalid request.
            var separator = dataUrl.IndexOf(";base64,", StringComparison.Ordinal);
            if (separator < 5)
            {
                continue;
            }

            try
            {
                images.Add(new ModelImage(
                    dataUrl.Substring(5, separator - 5),
                    dataUrl.Substring(separator + 8)));
            }
            catch (ArgumentException error)
            {
                throw new WorkerProtocolException("invalid_request", retryable: false, error);
            }
        }

        return images;
    }

    private static AssistantFinishReason MapFinishReason(string finishReason) => finishReason switch
    {
        "stop" => AssistantFinishReason.Stop,
        "tool_calls" => AssistantFinishReason.ToolCalls,
        "length" => AssistantFinishReason.Length,
        _ => throw new WorkerProtocolException("invalid_step"),
    };

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

    private static IReadOnlyList<string> RetainedAssistantEventIds(
        ModelContextSnapshot context,
        IReadOnlyList<ModelTurn> projectedMessages)
    {
        var projectedCallIds = projectedMessages.SelectMany(message => message.ToolCalls)
            .Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
        var steps = context.Entries.Where(entry => entry.Kind == ModelContextEntryKind.AssistantStep
            && entry.AssistantStep?.ToolCalls.Count > 0
            && entry.AssistantStep.ToolCalls.Any(call => projectedCallIds.Contains(call.ProviderCallId!))).ToArray();
        var eventIds = new List<string>(steps.Length);
        foreach (var step in steps)
        {
            if (step.AssistantStep!.ToolCalls.Any(call => !projectedCallIds.Contains(call.ProviderCallId!)))
                throw new WorkerProtocolException("provider_continuation_invalid");
            if (!step.SourceId.StartsWith("event:evt_", StringComparison.Ordinal)
                || step.SourceId.Length != 42)
                throw new WorkerProtocolException("provider_continuation_invalid");
            eventIds.Add(step.SourceId[6..]);
        }

        return eventIds;
    }

    private static (string Prompt, IReadOnlyList<ModelTurn> Messages) BuildTypedConversation(
        ModelContextSnapshot context,
        bool hasImages)
    {
        var units = new List<List<ModelTurn>>();
        var pendingUserBlocks = new List<string>();
        var pendingCalls = new Queue<ModelFunctionCall>();
        var providerCallIds = new HashSet<string>(StringComparer.Ordinal);
        var providerIdsByToolCall = new Dictionary<string, string>(StringComparer.Ordinal);
        string? protectedWorkState = null;
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
                    if (entry.SourceId == SessionWorkState.ContextSourceId)
                    {
                        AddPendingUserBlocks(units, pendingUserBlocks);
                        protectedWorkState = entry.Content;
                        units.Add([new ModelTurn("user", entry.Content)]);
                        continue;
                    }

                    pendingUserBlocks.Add(entry.Content);
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
                continue;
            }

            if (entry.Kind == ModelContextEntryKind.AssistantStep && entry.AssistantStep is { } step)
            {
                EnsureNoPendingCalls(pendingCalls);
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

        if (pendingUserBlocks.Count > 1)
        {
            foreach (var block in pendingUserBlocks.Take(pendingUserBlocks.Count - 1))
                units.Add([new ModelTurn("user", block)]);
        }
        var messages = FitModelMessagesToBudget(units);
        var currentPrompt = ResolveCurrentPrompt(pendingUserBlocks, messages, hasImages);
        if (protectedWorkState is not null
            && !messages.Any(message => message.Role == "user"
                && string.Equals(message.Content, protectedWorkState, StringComparison.Ordinal)))
            throw new WorkerProtocolException("request_too_large");
        return (currentPrompt, messages);
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
        var projectedContent = StructuredToolOutputProjector.Project(
            call.Name,
            call.Arguments,
            entry.Content ?? string.Empty,
            MaxConversationBlockChars,
            ToolOutputProjectionUnit.Utf16Characters,
            ClampConversationBlock,
            receipt.ToolCallId.Value,
            providerCallId);
        units[^1].Add(new ModelTurn(
            "tool",
            projectedContent,
            toolCallId: call.Id,
            toolResultCode: receipt.Code));
    }

    private static string ResolveCurrentPrompt(
        List<string> pendingUserBlocks,
        IReadOnlyList<ModelTurn> messages,
        bool hasImages)
    {
        if (pendingUserBlocks.Count > 0)
        {
            var currentPrompt = pendingUserBlocks[^1];
            if (currentPrompt.Length > ModelGenerationBounds.MaxPromptChars)
                throw new WorkerProtocolException("request_too_large");
            return currentPrompt;
        }
        if (messages.Count > 0 && messages[^1].Role == "tool") return DurableToolContinuationPrompt;
        var fallback = messages.LastOrDefault(message => message.Role == "user")?.Content;
        if (!string.IsNullOrWhiteSpace(fallback)) return fallback;
        return hasImages ? string.Empty : throw new WorkerProtocolException("missing_user_prompt");
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
            if (candidateCount > ModelGenerationBounds.MaxModelMessages
                || candidateBytes > ModelGenerationBounds.MaxModelMessagesBytes)
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
        var budget = Math.Max(1, ModelGenerationBounds.MaxPromptChars - note.Length);
        var body = prompt.Length <= budget ? prompt : ClampConversationBlock(prompt, budget);
        return body + note;
    }

    /// <summary>
    /// Keeps one conversation block inside the model prompt budget. Structured
    /// outputs carry a separate cursor when a later page can recover more content.
    /// </summary>
    private static string ClampConversationBlock(string block, int maxChars)
    {
        if (block.Length <= maxChars)
        {
            return block;
        }

        var prefixLength = Math.Max(0, maxChars - ConversationTruncationNote.Length);
        while (true)
        {
            prefixLength = ClampPrefixToUnicodeBoundary(block, prefixLength);
            var omitted = block.Length - prefixLength;
            var omission = $" (omitted {omitted} of {block.Length} characters)";
            var nextPrefixLength = maxChars - ConversationTruncationNote.Length - omission.Length;
            if (nextPrefixLength < 0)
                throw new WorkerProtocolException("model_history_too_large");

            nextPrefixLength = ClampPrefixToUnicodeBoundary(block, nextPrefixLength);
            if (nextPrefixLength == prefixLength)
                return block[..prefixLength] + ConversationTruncationNote + omission;

            prefixLength = nextPrefixLength;
        }
    }

    private static int ClampPrefixToUnicodeBoundary(string value, int prefixLength) =>
        prefixLength > 0
        && prefixLength < value.Length
        && char.IsHighSurrogate(value[prefixLength - 1])
        && char.IsLowSurrogate(value[prefixLength])
            ? prefixLength - 1
            : prefixLength;

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

            tools.Add(BuildModelTool(definition.ToolId, definition.ToolVersion, definition.Description, definition.Scope, definition.InputSchema));
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
        string description,
        string scope,
        JsonObject schema)
    {
        return new JsonObject
        {
            ["tool_id"] = toolId,
            ["tool_version"] = version,
            ["description"] = description,
            ["scopes"] = new JsonArray(scope),
            ["availability"] = "executable",
            ["model_enabled"] = true,
            ["capability_class"] = "tool",
            ["verification_required"] = false,
            ["input_schema"] = schema,
        };
    }

    private sealed record InProcessPreparedStep(WorkerV2PreparedRequest WorkerRequest)
        : IPreparedModelStep
    {
        public long EstimatedInputTokens => WorkerRequest.EstimatedInputTokens;
        public long MaxPromptTokens => WorkerRequest.MaxPromptTokens;
    }

    private sealed record NativeHistory(
        IReadOnlyList<JsonObject> Responses,
        IReadOnlyList<JsonObject> Anthropic);
}

/// <summary>
/// The bounded, stable failure carrier the Host maps into a run failure code.
/// Historical name kept: it is the code carrier the runtime drive map and the
/// terminal-code rules were written against.
/// </summary>
public sealed class WorkerProtocolException : Exception, IStableStepError
{
    public WorkerProtocolException(string code)
        : this(code, retryable: false)
    {
    }

    public WorkerProtocolException(string code, bool retryable)
        : this(code, retryable, innerException: null)
    {
    }

    public WorkerProtocolException(string code, bool retryable, Exception? innerException)
        : base(code, innerException)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }

    public string StepErrorCode => Code;

    /// <summary>Provider-reported transient signal (e.g. rate limits). Preserved
    /// so the step driver can retry bounded failures instead of failing the run.</summary>
    public bool Retryable { get; }
}

/// <summary>
/// Historical exception kept for the runtime drive map: nothing raises it now
/// that the worker is in process, so the <c>worker_unavailable</c> code can no
/// longer originate from a model step.
/// </summary>
public sealed class WorkerUnavailableException : Exception
{
    public WorkerUnavailableException(Exception? innerException = null)
        : base("Runtime worker is unavailable.", innerException)
    {
    }
}
