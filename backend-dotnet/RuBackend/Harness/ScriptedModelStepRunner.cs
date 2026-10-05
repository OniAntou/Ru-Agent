using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
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

    private readonly IReadOnlyList<AssistantStep> _steps;
    private readonly object _gate = new();
    private int _next;

    private ScriptedModelStepRunner(IReadOnlyList<AssistantStep> steps) => _steps = steps;

    internal static ScriptedModelStepRunner FromJsonForTests(string json) => new(Parse(json));

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

        return new ScriptedModelStepRunner(Parse(File.ReadAllText(path)));
    }

    /// <summary>
    /// Reads the script: a JSON array of steps.
    /// </summary>
    /// <remarks>
    /// Each step is <c>{ "text": "...", "tool": { "id": "...", "version": "...",
    /// "arguments": { ... } } }</c>. <c>tool</c> is optional; a step with one finishes as
    /// tool calls so the runtime executes it, and a step without one finishes the turn.
    /// In offline fixtures, <c>$projection_next_offset</c> resolves from the latest tool-result reference.
    /// </remarks>
    internal static IReadOnlyList<AssistantStep> Parse(string json)
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

        var steps = new List<AssistantStep>(array.Count);
        foreach (var node in array)
        {
            if (node is not JsonObject step)
            {
                throw new InvalidOperationException("Every scripted step must be a JSON object.");
            }

            string? text = step["text"]?.GetValue<string>();
            if (step["tool"] is not JsonObject tool)
            {
                steps.Add(new AssistantStep(
                    text,
                    Array.Empty<ModelToolCall>(),
                    AssistantFinishReason.Stop,
                    0));
                continue;
            }

            string toolId = tool["id"]?.GetValue<string>()
                ?? throw new InvalidOperationException("A scripted tool step needs \"id\".");
            string toolVersion = tool["version"]?.GetValue<string>() ?? "1.0.0";
            JsonElement arguments = tool["arguments"] is { } args
                ? JsonDocument.Parse(args.ToJsonString()).RootElement.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();

            var providerCallId = tool["provider_call_id"]?.GetValue<string>()
                ?? $"ru_call_script_{steps.Count + 1}";
            steps.Add(new AssistantStep(
                text,
                new[] { new ModelToolCall(providerCallId, toolId, toolVersion, arguments) },
                AssistantFinishReason.ToolCalls,
                0));
        }

        return steps;
    }

    public Task<ModelStepResult> RunStepAsync(
        ModelStepRequest request,
        Func<AssistantDelta, ValueTask> publishDelta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = publishDelta;
        cancellationToken.ThrowIfCancellationRequested();

        AssistantStep step;
        lock (_gate)
        {
            // Running out of script ends the turn rather than faulting the run: the script
            // is a fixture, and a fixture that is one step short should leave a finished
            // turn to inspect, not a failed one.
            step = _next < _steps.Count
                ? _steps[_next++]
                : new AssistantStep(
                    "Scripted run finished.",
                    Array.Empty<ModelToolCall>(),
                    AssistantFinishReason.Stop,
                    0);
        }

        // The coordinator validates this receipt against the prompt context it built, so an
        // empty or mismatched one fails the run as prompt_contract_invalid. The real runner
        // composes it in the Worker, where the provider's protocol decides whether the
        // capabilities block is rendered for native function calling. There is no provider
        // here, so both renderings are tried and the one that validates is used: the
        // difference is a single block, and the check is exactly what decides between them.
        step = ResolveProjectionCursor(step, request.PromptContext);
        var receipt = ComposeReceipt(request.PromptContext);
        return Task.FromResult(new ModelStepResult(step, receipt));
    }

    private AssistantStep ResolveProjectionCursor(AssistantStep step, SystemPromptContext promptContext)
    {
        var calls = step.ToolCalls.ToArray();
        var changed = false;
        var reference = promptContext.RuntimeContext.References.LastOrDefault(item => item.Kind == "tool_result");
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
                return TakeTerminalStep(step);
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

    private AssistantStep TakeTerminalStep(AssistantStep pagingStep)
    {
        lock (_gate)
        {
            while (_next < _steps.Count)
            {
                var terminal = _steps[_next++];
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
