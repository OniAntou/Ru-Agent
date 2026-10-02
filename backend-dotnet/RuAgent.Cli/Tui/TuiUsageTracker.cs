namespace RuAgent.Cli.Tui;

/// <summary>
/// Accumulates the header's usage cluster from the run-cumulative envelope counters: step
/// deltas feed the context meter and the published per-million rates feed the session cost.
/// A step delta is clamped at zero because every run restarts its counters; BeginTurn
/// resets that baseline while the session cost keeps accumulating across turns.
/// </summary>
public sealed class TuiUsageTracker
{
    private long _cumulativeInput;
    private long _cumulativeOutput;
    private long _maxStepTokens;

    /// <summary>Published context window for the active model, when the catalog has one.</summary>
    public long? ContextLimit { get; set; }

    /// <summary>Published rates, US dollars per million tokens, when the catalog has them.</summary>
    public double? InputRate { get; set; }

    public double? OutputRate { get; set; }

    public double SessionCost { get; private set; }

    /// <summary>True once at least one step was priced, so a free model still shows $0.00.</summary>
    public bool CostKnown { get; private set; }

    /// <summary>Context window usage of the largest step seen this turn, or null.</summary>
    public int? ContextPercent { get; private set; }

    public void BeginTurn()
    {
        _cumulativeInput = 0;
        _cumulativeOutput = 0;
        _maxStepTokens = 0;
        ContextPercent = null;
    }

    /// <summary>Feeds one usage envelope and recomputes the cluster.</summary>
    public void Apply(long? inputTokens, long? outputTokens)
    {
        var cumulativeInput = inputTokens ?? _cumulativeInput;
        var cumulativeOutput = outputTokens ?? _cumulativeOutput;
        var stepInput = Math.Max(0, cumulativeInput - _cumulativeInput);
        var stepOutput = Math.Max(0, cumulativeOutput - _cumulativeOutput);
        _cumulativeInput = cumulativeInput;
        _cumulativeOutput = cumulativeOutput;
        if (stepInput + stepOutput > 0)
        {
            _maxStepTokens = Math.Max(_maxStepTokens, stepInput + stepOutput);
        }

        if (InputRate is { } inputRate && OutputRate is { } outputRate)
        {
            SessionCost += ((stepInput * inputRate) + (stepOutput * outputRate)) / 1_000_000d;
            CostKnown = true;
        }

        ContextPercent = ContextLimit is { } limit and > 0 && _maxStepTokens > 0
            ? (int)Math.Clamp(Math.Round(100.0 * _maxStepTokens / limit), 0, 999)
            : null;
    }
}
