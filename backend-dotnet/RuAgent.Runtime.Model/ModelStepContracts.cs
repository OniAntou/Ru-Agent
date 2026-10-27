using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Models;

namespace RuAgent.Runtime.Model;

public sealed record ModelStepServiceResult(
    string? Text,
    IReadOnlyList<ModelToolCall> ToolCalls,
    string FinishReason,
    SystemPromptReceipt PromptReceipt,
    ModelUsage? Usage,
    string? ReasoningText = null,
    ProviderContinuation? ProviderContinuation = null,
    ResponsesAssistantMessages? ResponsesMessages = null);

public sealed class PreparedModelStep
{
    internal PreparedModelStep(
        ModelGenerationRequest source,
        ModelRequest request,
        SystemPromptSnapshot snapshot,
        JsonObject settings,
        IModelProvider provider,
        long estimatedInputTokens,
        long maxPromptTokens,
        string? contextScope = null)
    {
        Source = source;
        Request = request;
        Snapshot = snapshot;
        Settings = settings;
        Provider = provider;
        EstimatedInputTokens = estimatedInputTokens;
        MaxPromptTokens = maxPromptTokens;
        ContextScope = contextScope;
        ContextTokens = request.PreparedBodyJson is { } contextBody
            ? RequestContextEstimator.Measure(contextBody,
                request.AllImages.Sum(image => (long)Encoding.UTF8.GetByteCount(image.Base64Data)), request.AllImages.Count())
            : null;
        OutputTokenCap = request.MaximumOutputTokens;
        EffectiveReasoningEffort = ReadEffort(request.Settings);
        SelectedReasoningEffort = source.ProviderBootstrap.SelectedReasoningEffort;
        SerializedBodyBytes = request.PreparedBodyJson is { } body
            ? Encoding.UTF8.GetByteCount(body)
            : 0;
        SerializedBodySha256 = request.PreparedBodyJson is { } serializedBody
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serializedBody)))
                .ToLowerInvariant()
            : null;
    }

    internal ModelGenerationRequest Source { get; }
    internal ModelRequest Request { get; }
    internal SystemPromptSnapshot Snapshot { get; }
    internal JsonObject Settings { get; }
    internal IModelProvider Provider { get; }
    internal string? ContextScope { get; }
    public long EstimatedInputTokens { get; }
    public long MaxPromptTokens { get; }
    public ContextTokenBreakdown? ContextTokens { get; }
    public int SerializedBodyBytes { get; }
    public string? SerializedBodySha256 { get; }
    public int? OutputTokenCap { get; }
    public string EffectiveReasoningEffort { get; }
    public string SelectedReasoningEffort { get; }

    private static string ReadEffort(JsonObject settings)
    {
        var value = settings["reasoning_effort"]?.GetValue<string>()
            ?? settings["reasoning"]?["effort"]?.GetValue<string>();
        return value is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" ? value : "unknown";
    }
}

public sealed class ModelStepException : Exception, IStableStepError
{
    public ModelStepException(string code, bool retryable, Exception? innerException = null, string? trace = null, TimeSpan? retryAfter = null)
        : base($"model_step_{code}", innerException)
    {
        Code = code;
        Retryable = retryable;
        Trace = trace;
        RetryAfter = retryAfter;
    }

    public string Code { get; }

    public string StepErrorCode => Code;

    public bool Retryable { get; }

    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// Bounded, metadata-only diagnostic line for this failure (counts, flags, lengths,
    /// generated ids). Never carries user content, prompts, arguments or model text, so it
    /// is safe to write to the process stderr log. Null when the throw site predates tracing.
    /// The stderr prefix is <c>ru_model_step_trace</c>: the model step runs in the Host
    /// process, so this line is the bounded record that tells the three
    /// <c>protocol_failure</c> branches apart after the fact.
    /// </summary>
    public string? Trace { get; }
}
