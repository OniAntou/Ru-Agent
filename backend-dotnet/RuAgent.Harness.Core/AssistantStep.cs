using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace RuAgent.Harness.Core;

public enum AssistantFinishReason
{
    Stop,
    ToolCalls,
    Length,
    ContentFilter,
    Error,
}

public sealed record ModelToolCall
{
    public const int MaximumArgumentBytes = 1024 * 1024;

    public ModelToolCall(
        string? providerCallId,
        string toolId,
        string toolVersion,
        JsonElement arguments)
    {
        if (providerCallId is not null
            && (string.IsNullOrWhiteSpace(providerCallId) || providerCallId.Length > 256))
        {
            throw new ArgumentException(
                "Provider call identifier exceeds 256 characters.",
                nameof(providerCallId));
        }

        ProviderCallId = providerCallId;
        ToolId = RequireBounded(toolId, 128, nameof(toolId));
        ToolVersion = RequireBounded(toolVersion, 32, nameof(toolVersion));
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Tool arguments must be a JSON object.", nameof(arguments));
        }

        if (Encoding.UTF8.GetByteCount(arguments.GetRawText()) > MaximumArgumentBytes)
        {
            throw new ArgumentException("Tool arguments exceed the Core byte cap.", nameof(arguments));
        }

        Arguments = arguments.Clone();
    }

    public string? ProviderCallId { get; }

    public string ToolId { get; }

    public string ToolVersion { get; }

    public JsonElement Arguments { get; }

    private static string RequireBounded(string? value, int maximum, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
        {
            throw new ArgumentException(
                $"Value must contain between 1 and {maximum} characters.",
                parameterName);
        }

        return value;
    }
}

public sealed record AssistantStep
{
    public AssistantStep(
        string? text,
        IReadOnlyList<ModelToolCall> toolCalls,
        AssistantFinishReason finishReason,
        int estimatedInputTokens)
    {
        ArgumentNullException.ThrowIfNull(toolCalls);
        if (text is { Length: > 1_000_000 })
        {
            throw new ArgumentException("Assistant text exceeds the Core cap.", nameof(text));
        }

        if (!Enum.IsDefined(finishReason))
        {
            throw new ArgumentOutOfRangeException(nameof(finishReason));
        }

        if (estimatedInputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedInputTokens));
        }

        var calls = toolCalls.ToArray();
        if (calls.Any(call => call is null))
        {
            throw new ArgumentException("Tool call collection cannot contain null.", nameof(toolCalls));
        }

        if (finishReason == AssistantFinishReason.ToolCalls && calls.Length == 0)
        {
            throw new ArgumentException(
                "ToolCalls finish reason requires at least one call.",
                nameof(toolCalls));
        }

        Text = text;
        ToolCalls = new ReadOnlyCollection<ModelToolCall>(calls);
        FinishReason = finishReason;
        EstimatedInputTokens = estimatedInputTokens;
    }

    public string? Text { get; }

    public IReadOnlyList<ModelToolCall> ToolCalls { get; }

    public AssistantFinishReason FinishReason { get; }

    public int EstimatedInputTokens { get; }
}
