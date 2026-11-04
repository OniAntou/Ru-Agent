using RuAgent.Protocol.Harness;

namespace RuAgent.Client;

/// <summary>One request's context reading, independent of cumulative billing and transport cursors.</summary>
public sealed class RequestContextReading
{
    private readonly HashSet<string> _retired = new(StringComparer.Ordinal);
    private readonly Queue<string> _retiredOrder = new();
    private string? _activeStep;
    private string? _estimateStep;
    // Durable cursors order requests and compaction. Transient cursors only order one step's estimates.
    private long _startedSequence, _usageSequence, _boundarySequence, _estimateSequence;
    private bool _reportedForStep, _requiresStep;

    public long? Tokens { get; private set; }
    public bool IsEstimated { get; private set; }
    public bool HasReportedUsage => Tokens is not null && !IsEstimated;
    public ContextTokenBreakdown? Breakdown { get; private set; }
    public bool HasLiveData => _startedSequence > 0 || _usageSequence > 0 || _estimateSequence > 0 || _boundarySequence > 0;

    public void Reset(bool preserveReading = false)
    {
        _activeStep = _estimateStep = null;
        _startedSequence = _usageSequence = _boundarySequence = _estimateSequence = 0;
        _reportedForStep = _requiresStep = false;
        _retired.Clear(); _retiredOrder.Clear();
        if (!preserveReading) ClearReading();
    }

    public void Restore(long? tokens, ContextTokenBreakdown? breakdown)
    {
        if (HasLiveData) return;
        Tokens = tokens; IsEstimated = false; Breakdown = breakdown;
        _reportedForStep = tokens is not null;
    }

    public bool BeginStep(string? stepId, long sequence)
    {
        if (sequence <= _boundarySequence || sequence <= _startedSequence || sequence <= _usageSequence) return false;
        var step = SafeStep(stepId);
        if (step is not null && _retired.Contains(step)) return false;
        if (step is not null && step == _activeStep && _reportedForStep) return false;
        if (_activeStep != step) Retire(_activeStep);
        _activeStep = step;
        _estimateStep = null;
        _estimateSequence = 0;
        _startedSequence = sequence;
        _reportedForStep = _requiresStep = false;
        return true;
    }

    public bool ObserveEstimate(AgentUsageEstimateModel estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        var step = SafeStep(estimate.ModelStepId);
        if (step is null || estimate.EstimatedInputTokens <= 0 || estimate.TransientSequence <= 0
            || _requiresStep || _retired.Contains(step)
            || _activeStep is not null && _activeStep != step || _reportedForStep
            || _estimateStep == step && estimate.TransientSequence <= _estimateSequence) return false;
        _activeStep ??= step;
        _estimateStep = step;
        _estimateSequence = estimate.TransientSequence;
        Tokens = estimate.EstimatedInputTokens;
        IsEstimated = true;
        Breakdown = estimate.ContextTokens;
        return true;
    }

    public bool ObserveUsage(long sequence, string? stepId, long? promptTokens, ContextTokenBreakdown? breakdown)
    {
        var step = SafeStep(stepId);
        if (sequence <= _boundarySequence || sequence <= _usageSequence || sequence < _startedSequence
            || step is not null && (_retired.Contains(step) || _activeStep is not null && _activeStep != step)) return false;
        _usageSequence = sequence;
        _activeStep ??= step ?? _estimateStep;
        _reportedForStep = true;
        var hasCurrentEstimate = _estimateStep is not null && _estimateStep == _activeStep;
        // Missing input is not zero and must not erase an available prepared-request estimate.
        if (promptTokens is >= 0)
        {
            Tokens = promptTokens;
            IsEstimated = false;
        }
        else if (!hasCurrentEstimate) ClearReading();
        Breakdown = breakdown ?? (hasCurrentEstimate ? Breakdown : null);
        if (Tokens is null && Breakdown is not null)
        {
            Tokens = Breakdown.Total;
            IsEstimated = true;
        }
        return true;
    }

    public bool Compact(long sequence)
    {
        if (sequence <= _boundarySequence || sequence < _usageSequence || sequence < _startedSequence) return false;
        Invalidate(sequence);
        return true;
    }

    public void Invalidate(long sequence)
    {
        Retire(_activeStep);
        Retire(_estimateStep);
        _activeStep = _estimateStep = null;
        _boundarySequence = Math.Max(_boundarySequence, sequence);
        _usageSequence = Math.Max(_usageSequence, sequence);
        _reportedForStep = false;
        _requiresStep = true;
        ClearReading();
    }

    private void ClearReading() { Tokens = null; IsEstimated = false; Breakdown = null; }
    private static string? SafeStep(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl) ? value : null;
    private void Retire(string? step)
    {
        if (step is null || !_retired.Add(step)) return;
        _retiredOrder.Enqueue(step);
        // Late transient frames are bounded at the Host; do not retain an unbounded run history here.
        while (_retiredOrder.Count > 64) _retired.Remove(_retiredOrder.Dequeue());
    }
}
