using RuAgent.Protocol;

namespace RuAgent.Harness.Core.Context;

public enum ModelContextEntryKind
{
    Text,
    AssistantStep,
    ToolResult,
    Summary,
}

public enum ModelContextRole
{
    System,
    User,
    Assistant,
    Tool,
}

public sealed record ModelContextEntry
{
    private ModelContextEntry(
        string sourceId,
        long sequence,
        ModelContextRole role,
        ModelContextEntryKind kind,
        string? text,
        AssistantStep? assistantStep,
        ToolExecutionReceipt? toolReceipt,
        IReadOnlyList<ContextEntryRange>? summaryRanges,
        DateTimeOffset? executionStartedAtUtc = null)
    {
        if (!IsSourceId(sourceId))
        {
            throw new ArgumentException(
                "Context source ID must be a bounded opaque token.",
                nameof(sourceId));
        }

        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (text is { Length: > 4_000_000 })
        {
            throw new ArgumentException("Context text exceeds the Core cap.", nameof(text));
        }

        SourceId = sourceId;
        Sequence = sequence;
        Role = role;
        Kind = kind;
        Content = text;
        AssistantStep = assistantStep;
        ToolReceipt = toolReceipt;
        SummaryRanges = summaryRanges;
        ExecutionStartedAtUtc = executionStartedAtUtc;
    }

    public string SourceId { get; }

    public long Sequence { get; }

    public ModelContextRole Role { get; }

    public ModelContextEntryKind Kind { get; }

    public string? Content { get; }

    public AssistantStep? AssistantStep { get; }

    public ToolExecutionReceipt? ToolReceipt { get; }

    public IReadOnlyList<ContextEntryRange>? SummaryRanges { get; }

    /// <summary>Recorded request time for the execution that produced this receipt, when available.</summary>
    public DateTimeOffset? ExecutionStartedAtUtc { get; }

    /// <summary>The image a tool result carries into this request (ADR 0026).</summary>
    public ToolOutputImage? ToolImage { get; private init; }

    /// <summary>The tool result had an image that was left out of this request by name.</summary>
    public bool ToolImageOmitted { get; private init; }

    /// <summary>
    /// The same tool result with its stored image attached, or, when
    /// <paramref name="image"/> is null, marked as having an omitted image.
    /// </summary>
    public ModelContextEntry WithToolImage(ToolOutputImage? image)
    {
        if (Kind != ModelContextEntryKind.ToolResult)
        {
            throw new InvalidOperationException("Only tool results carry images.");
        }

        return new ModelContextEntry(SourceId, Sequence, Role, Kind, Content, null, ToolReceipt, null, ExecutionStartedAtUtc)
        {
            ToolImage = image,
            ToolImageOmitted = image is null,
        };
    }

    public static ModelContextEntry Text(
        string sourceId,
        long sequence,
        ModelContextRole role,
        string text)
    {
        if (role is not ModelContextRole.System and not ModelContextRole.User)
        {
            throw new ArgumentException("Text entries must be System or User.", nameof(role));
        }

        ArgumentNullException.ThrowIfNull(text);
        return new ModelContextEntry(
            sourceId,
            sequence,
            role,
            ModelContextEntryKind.Text,
            text,
            null,
            null,
            null);
    }

    public static ModelContextEntry Assistant(
        string sourceId,
        long sequence,
        AssistantStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return new ModelContextEntry(
            sourceId,
            sequence,
            ModelContextRole.Assistant,
            ModelContextEntryKind.AssistantStep,
            null,
            step,
            null,
            null);
    }

    public static ModelContextEntry ToolResult(
        string sourceId,
        long sequence,
        ToolExecutionReceipt receipt,
        string modelContent,
        DateTimeOffset? executionStartedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.ProviderCallId is null)
        {
            throw new ArgumentException("Tool results require a provider call relationship.", nameof(receipt));
        }

        ArgumentNullException.ThrowIfNull(modelContent);
        return new ModelContextEntry(
            sourceId,
            sequence,
            ModelContextRole.Tool,
            ModelContextEntryKind.ToolResult,
            modelContent,
            null,
            receipt,
            null,
            executionStartedAtUtc);
    }

    public static ModelContextEntry Summary(
        string compactionId,
        long projectionSequence,
        string text,
        IReadOnlyList<ContextEntryRange> coveredRanges)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(coveredRanges);
        if (coveredRanges.Count == 0)
        {
            throw new ArgumentException("Summary must cover at least one context range.", nameof(coveredRanges));
        }

        var copiedRanges = coveredRanges.ToArray();
        if (copiedRanges.Any(range => range is null))
        {
            throw new ArgumentException("Summary ranges cannot contain null.", nameof(coveredRanges));
        }

        return new ModelContextEntry(
            compactionId,
            projectionSequence,
            ModelContextRole.Tool,
            ModelContextEntryKind.Summary,
            text,
            null,
            null,
            copiedRanges);
    }

    private static bool IsSourceId(string? value)
    {
        if (value is not { Length: >= 1 and <= 128 })
        {
            return false;
        }

        return value.All(character =>
            character is (>= 'a' and <= 'z')
                or (>= 'A' and <= 'Z')
                or (>= '0' and <= '9')
                or '-'
                or '_'
                or '.'
                or ':');
    }
}
