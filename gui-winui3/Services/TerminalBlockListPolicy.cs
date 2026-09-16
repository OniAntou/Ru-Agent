namespace RuApp.Services;

/// <summary>
/// The terminal panel's block list: which block a streamed chunk belongs to, how a
/// block is labelled, and how the list is bounded.
///
/// The rules used to be inline in ChatView.ApplyAgentToolOutput and
/// CompleteTerminalBlock, where they could only be checked by reading those methods.
/// </summary>
public static class TerminalBlockListPolicy
{
    /// <summary>
    /// Oldest blocks are dropped past this many, so a long session cannot grow the
    /// panel without bound. The value is the long-standing one from the view.
    /// </summary>
    public const int MaximumBlocks = 5;

    /// <summary>Shown when the tool that produced the output was never labelled.</summary>
    public const string FallbackLabel = "Command output";

    /// <summary>Trims a tool label to something a panel header can show.</summary>
    public static string NormalizeLabel(string? toolLabel) =>
        string.IsNullOrWhiteSpace(toolLabel) ? FallbackLabel : toolLabel.Trim();

    /// <summary>
    /// Index of the block a chunk belongs to, or -1 when the execution has no block
    /// yet. Chunks are matched by execution id, so two concurrent terminal calls do
    /// not share one panel.
    /// </summary>
    public static int IndexOfExecution(IReadOnlyList<string> executionIds, string executionId)
    {
        ArgumentNullException.ThrowIfNull(executionIds);
        ArgumentNullException.ThrowIfNull(executionId);

        for (var index = 0; index < executionIds.Count; index++)
        {
            if (string.Equals(executionIds[index], executionId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>How many leading blocks to drop so the list fits the cap.</summary>
    public static int EvictionCount(int blockCount, int maximumBlocks = MaximumBlocks)
    {
        if (maximumBlocks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBlocks));
        }

        return blockCount > maximumBlocks ? blockCount - maximumBlocks : 0;
    }

    /// <summary>True while the list is at or past the cap, so an add needs an eviction.</summary>
    public static bool RequiresEviction(int blockCount, int maximumBlocks = MaximumBlocks) =>
        blockCount >= maximumBlocks;
}
