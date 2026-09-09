namespace RuBackend.Harness;

/// <summary>
/// Cộng dồn usage per SSE request. Thiếu field nào thì cộng đúng field đó,
/// không suy diễn. Chưa có số nào thì Snapshot trả null.
/// </summary>
public sealed class RunUsageTracker
{
    private long _input;
    private long _output;
    private long _total;
    private bool _hasInput;
    private bool _hasOutput;
    private bool _hasTotal;

    public void Track(RuAgent.Harness.Core.ModelUsage? usage)
    {
        if (usage is null) return;
        if (usage.InputTokens is { } input) { _input += input; _hasInput = true; }
        if (usage.OutputTokens is { } output) { _output += output; _hasOutput = true; }
        if (usage.TotalTokens is { } total) { _total += total; _hasTotal = true; }
    }

    public (long? InputTokens, long? OutputTokens, long? TotalTokens) Snapshot() =>
        (_hasInput ? _input : null, _hasOutput ? _output : null, _hasTotal ? _total : null);
}
