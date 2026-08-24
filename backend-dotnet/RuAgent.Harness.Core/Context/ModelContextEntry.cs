namespace RuAgent.Harness.Core.Context;

public enum ModelContextEntryKind
{
    Text,
    AssistantStep,
    ToolResult,
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
        ToolExecutionReceipt? toolReceipt)
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
    }

    public string SourceId { get; }

    public long Sequence { get; }

    public ModelContextRole Role { get; }

    public ModelContextEntryKind Kind { get; }

    public string? Content { get; }

    public AssistantStep? AssistantStep { get; }

    public ToolExecutionReceipt? ToolReceipt { get; }

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
            null);
    }

    public static ModelContextEntry ToolResult(
        string sourceId,
        long sequence,
        ToolExecutionReceipt receipt,
        string modelContent)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(modelContent);
        return new ModelContextEntry(
            sourceId,
            sequence,
            ModelContextRole.Tool,
            ModelContextEntryKind.ToolResult,
            modelContent,
            null,
            receipt);
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
