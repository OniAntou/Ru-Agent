namespace RuAgent.Runtime.Adapters.Models;

using RuAgent.Harness.Core;

public sealed record ModelDelta
{
    public ModelDelta(
        string? text,
        IReadOnlyList<ModelToolFragment> toolFragments,
        bool completed,
        string? finishReason,
        ModelUsage? usage = null,
        string? reasoning = null)
    {
        if (text is not null && (text.Length > 64 * 1024 || text.Contains('\0')))
        {
            throw new ArgumentException("Model delta exceeds the adapter cap.", nameof(text));
        }

        if (reasoning is not null && (reasoning.Length > 32 * 1024 || reasoning.Contains('\0')))
        {
            throw new ArgumentException("Model reasoning delta exceeds the adapter cap.", nameof(reasoning));
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
        Usage = usage;
        Reasoning = reasoning;
    }

    public string? Text { get; }

    public IReadOnlyList<ModelToolFragment> ToolFragments { get; }

    public bool Completed { get; }

    public string? FinishReason { get; }

    public ModelUsage? Usage { get; }

    /// <summary>Provider-supplied reasoning excerpt, if any. Observational
    /// only: never fed back into prompts, only surfaced as activity detail.</summary>
    public string? Reasoning { get; }
}
