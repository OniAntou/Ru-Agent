namespace RuAgent.Harness.Core.Reconciliation;

/// <summary>
/// Process-local mutation gate for runs whose ambiguous outcome the user answered with
/// UnknownContinue. While active, every non-read-only tool call for the run is denied
/// with a typed inspection requirement; exactly one durable read-only inspection
/// receipt clears it and normal evaluation resumes.
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
