namespace RuAgent.Harness.Core.Reconciliation;

/// <summary>
/// Process-local mutation gate for runs whose ambiguous outcome the user answered with
/// UnknownContinue. While active, every non-read-only tool call for the run is denied
/// with a typed inspection requirement; exactly one durable read-only inspection
/// receipt clears it and normal evaluation resumes.
///
/// Process-local is not the same as authoritative: the durable event stream is the
/// source of truth, and <see cref="ReconciliationGateProjection"/> re-arms this gate
/// from it at startup. Without that, a host restart silently lifted the gate and let
/// mutations through on a run the user had explicitly put under inspection.
/// </summary>
public sealed class ReconciliationInspectionGate
{
    private readonly object _lock = new();
    private readonly HashSet<string> _deniedRuns = new(StringComparer.Ordinal);

    public void Activate(RunId runId)
    {
        lock (_lock)
        {
            _deniedRuns.Add(runId.Value);
        }
    }

    /// <summary>Re-arms a whole startup batch under one lock acquisition.</summary>
    public void Seed(IEnumerable<RunId> runIds)
    {
        ArgumentNullException.ThrowIfNull(runIds);
        lock (_lock)
        {
            foreach (var runId in runIds)
            {
                _deniedRuns.Add(runId.Value);
            }
        }
    }

    public bool IsMutationDenied(RunId runId)
    {
        lock (_lock)
        {
            return _deniedRuns.Contains(runId.Value);
        }
    }

    public void RecordInspection(RunId runId)
    {
        lock (_lock)
        {
            _ = _deniedRuns.Remove(runId.Value);
        }
    }
}

/// <summary>
/// The explicit reconciliation transition for one run: Applied/NotApplied resolve the
/// ambiguity directly; UnknownContinue resumes evaluation under the mutation gate.
/// </summary>
public static class ReconciliationState
{
    public static string ResolutionCode(ReconciliationCommand command) => command.ToWireToken();
}

/// <summary>
/// Rebuilds the mutation-gate state of one run from its durable event stream. The
/// stream is the source of truth: the gate is armed by a reconciliation resolution
/// of <c>unknown_continue</c>, and cleared by the first durable succeeded read-only
/// tool receipt after it (or by an explicit user Continue, which records the
/// inspection itself).
/// </summary>
public static class ReconciliationGateProjection
{
    public const string UnknownContinueCode = "unknown_continue";

    /// <summary>
    /// Safe code of the durable event that records a read-only inspection clearing
    /// the gate. Emitted by the tool pipeline at the same commit as the receipt.
    /// </summary>
    public const string InspectionRecordedCode = "inspection_recorded";

    /// <summary>
    /// True when the run's own event history says its mutations are still gated.
    /// Events are projected in sequence order; the caller passes them in order.
    /// </summary>
    public static bool IsMutationDenied(IEnumerable<HarnessEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var armed = false;
        foreach (var item in events)
        {
            if (string.Equals(item.Kind, "reconciliation.resolved", StringComparison.Ordinal))
            {
                // Any later resolution replaces the earlier one: only an
                // unknown_continue resume puts mutations behind an inspection, and
                // an inspection receipt releases them again.
                armed = string.Equals(item.SafeCode, UnknownContinueCode, StringComparison.Ordinal);
            }
        }

        return armed;
    }
}
