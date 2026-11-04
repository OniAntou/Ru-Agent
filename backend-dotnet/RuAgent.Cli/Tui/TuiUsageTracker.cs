using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Tui;

/// <summary>
/// Per-request context and cumulative session billing have separate lifecycles.
/// </summary>
/// <remarks>
/// The SSE frame loop applies durable usage while optional metadata (provider
/// window, per-request prompt tokens) is fetched over HTTP in the background.
/// Both paths update the tracker, so every mutation and every read the renderer
/// performs takes the same lock. No member calls another public member while it
/// already holds the lock.
/// </remarks>
public sealed class TuiUsageTracker
{
    private readonly object _gate = new();
    private readonly RequestContextReading _context = new();
    private long _cumulativeInput, _cumulativeOutput;
    private string? _runId;
    private long? _contextLimit;
    private string? _providerProtocol;
    private double? _inputRate;
    private double? _outputRate;
    private double _sessionCost;
    private bool _costKnown;

    public long? ContextLimit
    {
        get { lock (_gate) return _contextLimit; }
        set { lock (_gate) _contextLimit = value; }
    }

    public string? ProviderProtocol
    {
        get { lock (_gate) return _providerProtocol; }
        set { lock (_gate) _providerProtocol = value; }
    }

    public double? InputRate
    {
        get { lock (_gate) return _inputRate; }
        set { lock (_gate) _inputRate = value; }
    }

    public double? OutputRate
    {
        get { lock (_gate) return _outputRate; }
        set { lock (_gate) _outputRate = value; }
    }

    public double SessionCost
    {
        get { lock (_gate) return _sessionCost; }
    }

    public bool CostKnown
    {
        get { lock (_gate) return _costKnown; }
    }

    public long? ContextTokens
    {
        get { lock (_gate) return _context.Tokens; }
    }

    public bool ContextIsEstimated
    {
        get { lock (_gate) return _context.IsEstimated; }
    }

    public double? ContextPercent
    {
        get
        {
            lock (_gate)
            {
                return _contextLimit is > 0 && _context.Tokens is { } tokens
                    ? Math.Clamp(100d * tokens / _contextLimit.Value, 0, 999)
                    : null;
            }
        }
    }

    /// <summary>Whether the tracker still belongs to this run, for deferred callbacks.</summary>
    public bool IsBoundTo(string runId)
    {
        lock (_gate)
        {
            return string.Equals(_runId, runId, StringComparison.Ordinal);
        }
    }

    public void BindRun(string runId)
    {
        lock (_gate)
        {
            if (_runId == runId) return;
            _runId = runId;
            _cumulativeInput = _cumulativeOutput = 0;
            // Keep the last reading while the next request is being prepared.
            _context.Reset(preserveReading: true);
        }
    }

    public bool BeginStep(string? stepId, long sequence)
    {
        lock (_gate) return _context.BeginStep(stepId, sequence);
    }

    public bool ObserveEstimate(AgentUsageEstimateModel estimate)
    {
        lock (_gate) return _context.ObserveEstimate(estimate);
    }

    public bool ObserveUsage(long sequence, string? stepId, long? tokens, ContextTokenBreakdown? breakdown)
    {
        lock (_gate) return _context.ObserveUsage(sequence, stepId, tokens, breakdown);
    }

    public bool Compact(long sequence)
    {
        lock (_gate) return _context.Compact(sequence);
    }

    /// <summary>Cumulative counters price the run; context comes from its per-request modelusage payload.</summary>
    public void ApplyBilling(long? inputTokens, long? outputTokens)
    {
        lock (_gate)
        {
            var input = inputTokens ?? _cumulativeInput;
            var output = outputTokens ?? _cumulativeOutput;
            var stepInput = Math.Max(0, input - _cumulativeInput);
            var stepOutput = Math.Max(0, output - _cumulativeOutput);
            _cumulativeInput = input;
            _cumulativeOutput = output;
            if (_inputRate is { } inputRate && _outputRate is { } outputRate)
            {
                _sessionCost += (stepInput * inputRate + stepOutput * outputRate) / 1_000_000d;
                _costKnown = true;
            }
        }
    }
}
