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
        // empty one fails the run as prompt_contract_invalid. The real runner composes it;
        // this one has to do the same rather than stub it out.
        var receipt = SystemPromptComposer.Compose(request.PromptContext).Receipt;
        return Task.FromResult(new ModelStepResult(step, receipt));
    }
}
