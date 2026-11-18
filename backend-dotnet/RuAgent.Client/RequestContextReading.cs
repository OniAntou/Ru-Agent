using RuAgent.Protocol.Harness;

namespace RuAgent.Client;

/// <summary>One request's context reading, independent of cumulative billing and transport cursors.</summary>
/// <remarks>
/// <para>
/// The figure a meter shows is the size of the run's next request in the provider's own
/// tokens. Three corrections keep it close to what the provider will count:
/// </para>
/// <list type="bullet">
/// <item>The Host's pre-request estimate is a bytes/4 heuristic that never undercounts, which
/// the admission and compaction checks need. A meter that showed it raw jumped up at every
/// step start and fell back when the provider reported (about +12% on a model that counts
/// fewer tokens than bytes/4). Each report carries the heuristic of the same request, so their
/// ratio converts the next estimate into provider tokens. Only the display changes: the
/// estimate the Host acts on is untouched.</item>
/// <item>A reported prompt does not contain the reply it produced. When the client knows the
/// reply's visible text, it adds that as an estimated tail until the next request is
/// measured. Output tokens are not used: much of them is reasoning that is not replayed.</item>
/// <item>The compaction trigger the Host published with the estimate is converted with the
/// same ratio, so a meter can mark it in the units it shows.</item>
/// </list>
/// </remarks>
public sealed class RequestContextReading
{
    /// <summary>Bounds on the provider-to-heuristic ratio; outside them a report is not trusted.</summary>
    public const double MinimumRatio = 0.5, MaximumRatio = 2.0;

    private readonly HashSet<string> _retired = new(StringComparer.Ordinal);
    private readonly Queue<string> _retiredOrder = new();
    private string? _activeStep;
    private string? _estimateStep;
    private string? _route;
    // Durable cursors order requests and compaction. Transient cursors only order one step's estimates.
    private long _startedSequence, _usageSequence, _boundarySequence, _estimateSequence;
    private bool _reportedForStep, _requiresStep;
    private long? _rawTrigger;
    private double? _triggerScale;

    public long? Tokens { get; private set; }
    public bool IsEstimated { get; private set; }
    public bool HasReportedUsage => Tokens is not null && !IsEstimated;
    public ContextTokenBreakdown? Breakdown { get; private set; }
    public bool HasLiveData => _startedSequence > 0 || _usageSequence > 0 || _estimateSequence > 0 || _boundarySequence > 0;

    /// <summary>Provider tokens per heuristic token, learned from the route's last report.</summary>
    public double? Ratio { get; private set; }

    /// <summary>Estimated tokens of the reply the latest reading does not contain yet.</summary>
    public long TailTokens { get; private set; }

    /// <summary>The reading plus the reply tail: the size of the next request as far as it is known.</summary>
    public long? ContextTokens => Tokens is { } tokens ? tokens + TailTokens : null;

    /// <summary>The prompt budget the Host enforces for this route, from its latest estimate.</summary>
    public long? MaxPromptTokens { get; private set; }

    /// <summary>
    /// The compaction trigger in the units of <see cref="ContextTokens"/>, when the Host
    /// published one: the raw trigger converted with the same ratio as the estimate.
    /// </summary>
    public long? CompactionTriggerTokens =>
        _rawTrigger is { } raw ? (long)Math.Round(raw * (_triggerScale ?? Ratio ?? 1.0)) : null;

    /// <summary>
    /// The breakdown scaled so its parts sum to <see cref="ContextTokens"/>; the reply tail is
    /// part of the messages. Null when either side is unknown.
    /// </summary>
    public ContextTokenBreakdown? DisplayBreakdown
    {
        get
        {
            if (Breakdown is not { Total: > 0 } breakdown || Tokens is not { } tokens) return Breakdown;
            var scale = (double)tokens / breakdown.Total;
            int Part(int value) => (int)Math.Min(int.MaxValue, Math.Round(value * scale));
            var system = Part(breakdown.SystemPrompt);
            var tools = Part(breakdown.ToolDefinitions);
            var messages = (int)Math.Clamp(tokens + TailTokens - system - tools, 0, int.MaxValue);
            return new ContextTokenBreakdown(system, tools, messages);
        }
    }

    /// <summary>
    /// Names the provider route (protocol and model) the reading belongs to. A different
    /// route counts tokens differently, so its ratio and budget are forgotten.
    /// </summary>
    public void UseRoute(string? route)
    {
        if (string.Equals(_route, route, StringComparison.Ordinal)) return;
        _route = route;
        Ratio = null;
        MaxPromptTokens = null;
        _rawTrigger = null;
        _triggerScale = null;
        if (route is not null && ContextBaselineMemory.RatioFor(route) is { } remembered) Ratio = remembered;
    }

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
        Tokens = tokens; IsEstimated = false; Breakdown = breakdown; TailTokens = 0;
        _reportedForStep = tokens is not null;
        Learn(tokens, breakdown);
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
        // The heuristic of this request is the breakdown's total; the published estimate may
        // already be calibrated upward for the Host's own checks, so it is only the fallback.
        var display = Ratio is { } ratio && estimate.ContextTokens is { Total: > 0 } parts
            ? Math.Max(1, (long)Math.Round(parts.Total * ratio))
            : estimate.EstimatedInputTokens;
        Tokens = display;
        IsEstimated = true;
        TailTokens = 0;
        Breakdown = estimate.ContextTokens;
        MaxPromptTokens = estimate.MaxPromptTokens > 0 ? estimate.MaxPromptTokens : MaxPromptTokens;
        _rawTrigger = estimate.CompactionTriggerTokens ?? _rawTrigger;
        _triggerScale = (double)display / estimate.EstimatedInputTokens;
        RememberBaseline();
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
        TailTokens = 0;
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

        Learn(promptTokens, breakdown);
        return true;
    }

    /// <summary>
    /// Adds the reply the latest reading does not contain, from its visible character count.
    /// Ignored while no reading exists or the reading is a pre-request estimate, which already
    /// covers everything sent so far.
    /// </summary>
    public void ObserveReplyTail(long characters)
    {
        if (Tokens is null || IsEstimated) { TailTokens = 0; return; }
        var heuristic = characters <= 0 ? 0 : (characters + 3) / 4;
        TailTokens = (long)Math.Round(heuristic * (Ratio ?? 1.0));
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

    private void Learn(long? promptTokens, ContextTokenBreakdown? breakdown)
    {
        if (promptTokens is not > 0 || breakdown is not { Total: > 0 }) return;
        var ratio = (double)promptTokens.Value / breakdown.Total;
        if (ratio is < MinimumRatio or > MaximumRatio) return;
        Ratio = ratio;
        if (_route is not null) ContextBaselineMemory.RememberRatio(_route, ratio);
        RememberBaseline();
    }

    private void RememberBaseline()
    {
        if (_route is not null && Breakdown is { } parts)
            ContextBaselineMemory.RememberBaseline(_route, parts.SystemPrompt + (long)parts.ToolDefinitions, Ratio);
    }

    private void ClearReading() { Tokens = null; IsEstimated = false; Breakdown = null; TailTokens = 0; }
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
