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

    private readonly IReadOnlyList<AssistantStep> _steps;
    private readonly object _gate = new();
    private int _next;

    private ScriptedModelStepRunner(IReadOnlyList<AssistantStep> steps) => _steps = steps;

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

            steps.Add(new AssistantStep(
                text,
                new[] { new ModelToolCall(null, toolId, toolVersion, arguments) },
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
        var receipt = ComposeReceipt(request.PromptContext);
        return Task.FromResult(new ModelStepResult(step, receipt));
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
