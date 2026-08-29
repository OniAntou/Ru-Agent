namespace RuAgent.Runtime.Adapters.Models;

public sealed record ModelDelta
{
    public ModelDelta(
        string? text,
        IReadOnlyList<ModelToolFragment> toolFragments,
        bool completed,
        string? finishReason)
    {
        if (text is not null && (text.Length > 64 * 1024 || text.Contains('\0')))
        {
            throw new ArgumentException("Model delta exceeds the adapter cap.", nameof(text));
        }

        ArgumentNullException.ThrowIfNull(toolFragments);
        if (toolFragments.Count > 16 || toolFragments.Any(fragment => fragment is null))
        {
            throw new ArgumentException("Model tool fragments exceed the adapter cap.", nameof(toolFragments));
        }

        if (finishReason is not null && finishReason is not ("stop" or "tool_calls" or "length"))
        {
            throw new ArgumentException("Model finish reason is invalid.", nameof(finishReason));
        }

        Text = text;
        ToolFragments = toolFragments.ToArray();
        Completed = completed;
        FinishReason = finishReason;
    }

    public string? Text { get; }

    public IReadOnlyList<ModelToolFragment> ToolFragments { get; }

    public bool Completed { get; }

    public string? FinishReason { get; }
}
