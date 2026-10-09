using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;

namespace RuBackend.Harness;

/// <summary>
/// A model that reads its steps from a JSON file instead of calling a provider.
/// </summary>
/// <remarks>
/// This exists so the whole product can be exercised without a provider key: start the app,
/// let a run execute real tools through the real Broker, and watch what the interface does
/// with the result. The alternative is a suite that can assert everything except what the
/// user actually sees, which is how a feed that never rendered stayed green.
///
/// It is opt-in through <see cref="EnvironmentVariable"/> and refuses to start otherwise.
/// Nothing here runs in a normal session, and the file it reads is a test fixture, not
/// configuration: a missing or malformed script is a startup error rather than a silent
/// fall back to a real provider.
/// </remarks>
public sealed class ScriptedModelStepRunner : IModelStepRunner
{
    public const string EnvironmentVariable = "RU_SCRIPTED_MODEL";
    private const string ProjectionCursorPlaceholder = "$projection_next_offset";
    private const string ProjectionSpoolIdPlaceholder = "$projection_spool_id";
    private const int MaxScriptedCallsPerStep = 8;

    private readonly IReadOnlyList<ScriptedStep> _steps;
    private readonly object _gate = new();
    private int _next;

    private ScriptedModelStepRunner(IReadOnlyList<ScriptedStep> steps) => _steps = steps;

    internal static ScriptedModelStepRunner FromJsonForTests(string json) => new(ParseScript(json));

    /// <summary>
    /// Builds a runner from the environment, or returns null when it is not requested.
    /// </summary>
    public static ScriptedModelStepRunner? FromEnvironment()
    {
        string? path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} points at '{path}', which does not exist.");
        }

        return new ScriptedModelStepRunner(ParseScript(File.ReadAllText(path)));
    }

    /// <summary>
    /// Reads the script: a JSON array of steps.
    /// </summary>
    /// <remarks>
    /// Each step has optional text and either one <c>tool</c> or a bounded ordered
    /// <c>tools</c> array. A tool step asks the runtime to execute the listed calls.
    /// Paging placeholders resolve only from the explicitly named preceding tool call.
    /// </remarks>
    internal static IReadOnlyList<AssistantStep> Parse(string json)
        => ParseScript(json).Select(item => item.Step).ToArray();

    private static IReadOnlyList<ScriptedStep> ParseScript(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"{EnvironmentVariable} is not valid JSON.", error);
        }

        if (root is not JsonArray array)
        {
            throw new InvalidOperationException($"{EnvironmentVariable} must hold a JSON array of steps.");
        }

        var steps = new List<ScriptedStep>(array.Count);
        foreach (var node in array)
        {
            if (node is not JsonObject step)
            {
                throw new InvalidOperationException("Every scripted step must be a JSON object.");
            }

            string? text = step["text"]?.GetValue<string>();
            if (step["tool"] is not null && step["tools"] is not null)
            {
                throw new InvalidOperationException("A scripted step must use either \"tool\" or \"tools\", not both.");
            }

            var tools = ReadTools(step, steps.Count);
            var cursorSourceCallId = ReadCursorSourceCallId(step, steps, tools);
            if (tools.Count == 0)
            {
                steps.Add(new ScriptedStep(new AssistantStep(
                    text,
                    Array.Empty<ModelToolCall>(),
                    AssistantFinishReason.Stop,
                    0), ScriptedContextOracle.Read(step), cursorSourceCallId));
                continue;
            }

            steps.Add(new ScriptedStep(new AssistantStep(
                text,
                tools,
                AssistantFinishReason.ToolCalls,
                0), ScriptedContextOracle.Read(step), cursorSourceCallId));
        }

        return steps;
    }

    private static List<ModelToolCall> ReadTools(JsonObject step, int stepIndex)
    {
        if (step.ContainsKey("tool") && step["tool"] is not JsonObject)
        {
            throw new InvalidOperationException("A scripted \"tool\" must be an object.");
        }

        if (step["tool"] is JsonObject singleTool)
        {
            return [ReadTool(singleTool, stepIndex, 0, singleCall: true)];
        }

        if (step.ContainsKey("tools") && step["tools"] is not JsonArray)
        {
            throw new InvalidOperationException("A scripted \"tools\" value must be an array.");
        }

        if (step["tools"] is not JsonArray toolArray)
        {
            return [];
        }

        if (toolArray.Count is < 1 or > MaxScriptedCallsPerStep)
        {
            throw new InvalidOperationException($"A scripted tool step must contain between one and {MaxScriptedCallsPerStep} calls.");
        }

        var calls = new List<ModelToolCall>(toolArray.Count);
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < toolArray.Count; index++)
        {
            if (toolArray[index] is not JsonObject tool)
            {
                throw new InvalidOperationException("Every scripted tool call must be an object.");
            }

            var call = ReadTool(tool, stepIndex, index, singleCall: false);
            if (!callIds.Add(call.ProviderCallId))
            {
                throw new InvalidOperationException("Scripted provider call IDs must be unique within a step.");
            }

            calls.Add(call);
        }

        return calls;
    }

    private static ModelToolCall ReadTool(JsonObject tool, int stepIndex, int callIndex, bool singleCall)
    {
        var toolId = tool["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("A scripted tool call needs \"id\".");
        var toolVersion = tool["version"]?.GetValue<string>() ?? "1.0.0";
        var arguments = tool["arguments"] is { } args
            ? JsonDocument.Parse(args.ToJsonString()).RootElement.Clone()
            : JsonDocument.Parse("{}").RootElement.Clone();
        var generatedId = singleCall
            ? $"ru_call_script_{stepIndex + 1}"
            : $"ru_call_script_{stepIndex + 1}_{callIndex + 1}";
        var providerCallId = tool["provider_call_id"]?.GetValue<string>() ?? generatedId;
        if (string.IsNullOrWhiteSpace(providerCallId) || providerCallId.Length > 128)
        {
            throw new InvalidOperationException("A scripted provider call ID must be bounded and non-empty.");
        }

        return new ModelToolCall(providerCallId, toolId, toolVersion, arguments);
    }

    public Task<ModelStepResult> RunStepAsync(
        ModelStepRequest request,
        Func<AssistantDelta, ValueTask> publishDelta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = publishDelta;
        cancellationToken.ThrowIfCancellationRequested();

        ScriptedStep scriptedStep;
        lock (_gate)
        {
            var durablePosition = request.Context.Entries.Count(entry =>
                entry.Kind == ModelContextEntryKind.AssistantStep
                && entry.SourceId.StartsWith("event:evt_", StringComparison.Ordinal));
            _next = Math.Max(_next, durablePosition);
            // Running out of script ends the turn rather than faulting the run: the script
            // is a fixture, and a fixture that is one step short should leave a finished
            // turn to inspect, not a failed one.
            scriptedStep = _next < _steps.Count
                ? _steps[_next++]
                : new ScriptedStep(new AssistantStep(
                    "Scripted run finished.",
                    Array.Empty<ModelToolCall>(),
                    AssistantFinishReason.Stop,
                    0), null, null);
        }
        scriptedStep.ContextOracle?.Validate(request.Context, request.PromptContext);

        // The coordinator validates this receipt against the prompt context it built, so an
        // empty or mismatched one fails the run as prompt_contract_invalid. The real runner
        // composes it in the model step, where the provider's protocol decides whether the
        // capabilities block is rendered for native function calling. There is no provider
        // here, so both renderings are tried and the one that validates is used: the
        // difference is a single block, and the check is exactly what decides between them.
        var step = ResolveProjectionCursor(
            scriptedStep.Step,
            request.Context,
            request.PromptContext,
            scriptedStep.CursorSourceCallId);
        var receipt = ComposeReceipt(request.PromptContext);
        return Task.FromResult(new ModelStepResult(step, receipt));
    }

    private static string? ReadCursorSourceCallId(
        JsonObject step,
        IReadOnlyList<ScriptedStep> previousSteps,
        IReadOnlyList<ModelToolCall> currentCalls)
    {
        var sourceCallId = step["cursor_from_call_id"]?.GetValue<string>();
        var hasPlaceholder = currentCalls.Any(call =>
            IsPlaceholder(call.Arguments, "offset", ProjectionCursorPlaceholder)
            || IsPlaceholder(call.Arguments, "execution_id", ProjectionSpoolIdPlaceholder));
        if (!hasPlaceholder)
        {
            if (sourceCallId is not null) throw new InvalidOperationException("A cursor source requires a paging placeholder.");
            return null;
        }

        sourceCallId ??= previousSteps.LastOrDefault()?.Step.ToolCalls.LastOrDefault()?.ProviderCallId;
        if (sourceCallId is not { Length: > 0 and <= 128 }
            || !previousSteps.Any(previous => previous.Step.ToolCalls.Any(call =>
                string.Equals(call.ProviderCallId, sourceCallId, StringComparison.Ordinal))))
            throw new InvalidOperationException("A paging placeholder needs an exact preceding provider call identity.");
        return sourceCallId;
    }

    private sealed record ScriptedStep(AssistantStep Step, ScriptedContextOracle? ContextOracle, string? CursorSourceCallId);

    private AssistantStep ResolveProjectionCursor(
        AssistantStep step,
        ModelContextSnapshot context,
        SystemPromptContext promptContext,
        string? cursorSourceCallId)
    {
        var calls = step.ToolCalls.ToArray();
        var changed = false;
        var needsPaging = calls.Any(call =>
            IsPlaceholder(call.Arguments, "offset", ProjectionCursorPlaceholder)
            || IsPlaceholder(call.Arguments, "execution_id", ProjectionSpoolIdPlaceholder));
        var expectedReceiptEntry = needsPaging
            ? context.Entries
                .Where(entry => entry.Kind == ModelContextEntryKind.ToolResult
                    && string.Equals(entry.ToolReceipt?.ProviderCallId, cursorSourceCallId, StringComparison.Ordinal))
                .OrderBy(entry => entry.Sequence)
                .LastOrDefault()
            : null;
        var expectedToolCallId = expectedReceiptEntry?.ToolReceipt?.ToolCallId.Value;
        var reference = needsPaging
            ? promptContext.RuntimeContext.References.SingleOrDefault(item =>
                item.Kind == "tool_result"
                && (ScriptedContextOracle.HasProjectionCallIdentity(item.Content, cursorSourceCallId, expectedToolCallId)
                    || (expectedReceiptEntry?.Content is { } sourceContent
                        && !ScriptedContextOracle.HasProjectionIdentity(item.Content)
                        && string.Equals(item.Content, sourceContent, StringComparison.Ordinal))))
            : null;
        if (needsPaging && reference is null)
            throw new InvalidOperationException("scripted_projection_cursor_source_unavailable");
        for (var index = 0; index < calls.Length; index++)
        {
            var call = calls[index];
            if (reference is null || call.Arguments.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var needsCursor = IsPlaceholder(call.Arguments, "offset", ProjectionCursorPlaceholder);
            var needsSpoolId = IsPlaceholder(call.Arguments, "execution_id", ProjectionSpoolIdPlaceholder);
            if (!needsCursor && !needsSpoolId)
                continue;

            var cursor = 0;
            var hasMore = false;
            if (needsCursor && !TryReadProjectionCursor(reference.Content, out cursor, out hasMore))
                throw new InvalidOperationException("scripted_projection_cursor_unavailable");
            if (needsCursor && !hasMore)
            {
                return TakeTerminalStep(step, context, promptContext);
            }

            var arguments = JsonNode.Parse(call.Arguments.GetRawText())!.AsObject();
            if (needsCursor)
                arguments["offset"] = cursor;
            if (needsSpoolId)
                arguments["execution_id"] = ReadSpoolId(reference.Content);
            using var parsedArguments = JsonDocument.Parse(arguments.ToJsonString());
            calls[index] = new ModelToolCall(call.ProviderCallId, call.ToolId, call.ToolVersion, parsedArguments.RootElement);
            changed = true;
        }

        return changed
            ? new AssistantStep(step.Text, calls, step.FinishReason, step.EstimatedInputTokens, step.Usage)
            : step;
    }

    private AssistantStep TakeTerminalStep(
        AssistantStep pagingStep,
        ModelContextSnapshot context,
        SystemPromptContext promptContext)
    {
        lock (_gate)
        {
            while (_next < _steps.Count)
            {
                var scriptedTerminal = _steps[_next++];
                scriptedTerminal.ContextOracle?.Validate(context, promptContext);
                var terminal = scriptedTerminal.Step;
                if (terminal.ToolCalls.Count == 1
                    && terminal.ToolCalls[0].Arguments.ValueKind == JsonValueKind.Object
                    && IsPlaceholder(terminal.ToolCalls[0].Arguments, "offset", ProjectionCursorPlaceholder))
                {
                    continue;
                }

                if (terminal.ToolCalls.Count > 0)
                    throw new InvalidOperationException("scripted_projection_terminal_step_invalid");
                return new AssistantStep(terminal.Text, Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop,
                    terminal.EstimatedInputTokens, terminal.Usage);
            }
        }

        return new AssistantStep(pagingStep.Text, Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop,
            pagingStep.EstimatedInputTokens, pagingStep.Usage);
    }

    private static bool IsPlaceholder(JsonElement arguments, string propertyName, string placeholder) =>
        arguments.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), placeholder, StringComparison.Ordinal);

    private static bool TryReadProjectionCursor(string content, out int cursor, out bool hasMore)
    {
        cursor = 0;
        hasMore = false;

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.TryGetProperty("projection_next_offset", out var cursorProperty)
                && cursorProperty.TryGetInt32(out var value)
                && value >= 0
                && root.TryGetProperty("projection_has_more", out var hasMoreProperty)
                && (hasMoreProperty.ValueKind == JsonValueKind.True || hasMoreProperty.ValueKind == JsonValueKind.False))
            {
                cursor = value;
                hasMore = hasMoreProperty.ValueKind == JsonValueKind.True;
                return true;
            }

            if (IsCompleteStructuredResult(root))
                return true;
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool IsCompleteStructuredResult(JsonElement root) =>
        root.TryGetProperty("truncated", out var truncated)
        && truncated.ValueKind == JsonValueKind.False
        && ((root.TryGetProperty("results", out _) && root.TryGetProperty("count", out _))
            || (root.TryGetProperty("content", out _) && root.TryGetProperty("path", out _))
            || (root.TryGetProperty("page", out _) && root.TryGetProperty("spool_id", out _)));

    private static string ReadSpoolId(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("spool_id", out var spool)
                && spool.ValueKind == JsonValueKind.String
                && spool.GetString() is { Length: > 0 and <= 256 } value)
            {
                return value;
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("scripted_projection_cursor_unavailable");
        }

        throw new InvalidOperationException("scripted_projection_cursor_unavailable");
    }

    /// <summary>
    /// Composes the receipt the coordinator will check this step against.
    /// </summary>
    /// <remarks>
    /// Internal so the receipt rule can be tested directly. A mismatch here fails a run as
    /// prompt_contract_invalid, which is a confusing way to learn that a fixture is wrong.
    /// </remarks>
    internal static SystemPromptReceipt ComposeReceiptForTests(SystemPromptContext context) =>
        ComposeReceipt(context);

    private static SystemPromptReceipt ComposeReceipt(SystemPromptContext context)
    {
        string? firstFailure = null;
        foreach (bool nativeToolCalling in new[] { true, false })
        {
            var candidate = SystemPromptComposer.Compose(context, nativeToolCalling).Receipt;
            try
            {
                candidate.ValidateAgainst(context);
                return candidate;
            }
            catch (ArgumentException error)
            {
                // The other rendering may be the one the run was started with.
                firstFailure ??= $"{nativeToolCalling}: {error.Message}";
            }
        }

        // Neither matched, so the run is going to fail its contract check either way.
        // Returning the text-mode receipt keeps the failure at the coordinator, where it
        // belongs, instead of throwing a different error from inside the model.
        //
        // The reason is recorded because the coordinator reports only
        // prompt_contract_invalid: that says a receipt was rejected but not which rule it
        // broke, and diagnosing a fixture without it means bisecting the prompt by hand.
        TryRecordFailure(context, firstFailure);
        return SystemPromptComposer.Compose(context).Receipt;
    }

    private static void TryRecordFailure(SystemPromptContext context, string? reason)
    {
        try
        {
            var snapshot = SystemPromptComposer.Compose(context, nativeToolCalling: true);
            var receipt = snapshot.Receipt;
            string sizes = string.Join(
                ",",
                receipt.BlockDigests.Select(d => $"{d.BlockId}={d.Sha256.Length}"));

            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "ru-scripted-receipt-failure.txt"),
                $"{DateTime.UtcNow:O} receipt rejected: {reason}{Environment.NewLine}" +
                $"  promptTextBytes={System.Text.Encoding.UTF8.GetByteCount(snapshot.Text)}{Environment.NewLine}" +
                $"  instructionBlocks={context.InstructionBlocks.Count}" +
                $" capabilities={context.Capabilities.Count}" +
                $" profile={context.AgentProfile}{Environment.NewLine}" +
                $"  blocks={sizes}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Diagnostics must never be the reason a run fails.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
