namespace RuAgent.Harness.Core.Context;

/// <summary>Retains required evidence independently from bounded recent diagnostics.</summary>
internal sealed class VerificationEvidenceBuffer(int requiredLimit)
{
    private const int DiagnosticLimit = 64;
    private readonly Dictionary<string, VerificationCheckEvidence> _checks = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _diagnostics = new();
    private readonly Dictionary<string, LinkedListNode<string>> _diagnosticNodes = new(StringComparer.Ordinal);
    private readonly Queue<string> _expectedOrder = new();
    private int _requiredCount;

    internal HashSet<string> ExpectedFailures { get; } = new(StringComparer.Ordinal);
    internal IEnumerable<VerificationCheckEvidence> Checks => _checks.Values;
    internal bool Overflowed { get; private set; }
    internal VerificationCheckEvidence? Find(string identity) => _checks.GetValueOrDefault(identity);

    internal void Seed(IEnumerable<VerificationCheckEvidence> checks, IEnumerable<string> expectedFailures, bool overflowed)
    {
        foreach (var check in checks) Store(check);
        foreach (var receiptId in expectedFailures) RememberExpectedFailure(receiptId);
        Overflowed |= overflowed;
    }

    private static bool Required(VerificationCheckEvidence check) => check.IsVerification
        || check.ExpectedExitCode != 0 && check.Status != VerificationCheckStatus.Passed;

    internal void Store(VerificationCheckEvidence check)
    {
        var old = Find(check.Identity);
        if (Required(check) && (old is null || !Required(old)) && _requiredCount >= requiredLimit)
        {
            Overflowed = true;
            return;
        }
        Remove(check.Identity);
        _checks[check.Identity] = check;
        if (Required(check)) _requiredCount++;
        else _diagnosticNodes[check.Identity] = _diagnostics.AddLast(check.Identity);
        while (_diagnostics.Count > DiagnosticLimit) Remove(_diagnostics.First!.Value);
    }

    internal VerificationCheckEvidence[] Pending(string executionId) => _checks.Values
        .Where(c => c.ExecutionId == executionId && c.Status == VerificationCheckStatus.Pending).ToArray();

    internal void RememberExpectedFailure(string receiptId)
    {
        if (!ExpectedFailures.Add(receiptId)) return;
        _expectedOrder.Enqueue(receiptId);
        // Completion inspects the latest tool batch, bounded by the same 256-call
        // window. Older receipt IDs are audit history, not outstanding checks.
        while (_expectedOrder.Count > requiredLimit) ExpectedFailures.Remove(_expectedOrder.Dequeue());
    }

    private void Remove(string identity)
    {
        if (!_checks.Remove(identity, out var old)) return;
        if (Required(old)) _requiredCount--;
        if (_diagnosticNodes.Remove(identity, out var node)) _diagnostics.Remove(node);
    }
}
