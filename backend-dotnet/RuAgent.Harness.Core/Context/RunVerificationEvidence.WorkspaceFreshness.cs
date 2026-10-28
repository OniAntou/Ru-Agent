namespace RuAgent.Harness.Core.Context;

public readonly record struct WorkspaceCheckFreshness(bool IsKnown, bool ChangedAfterStart);

public sealed partial class RunVerificationEvidence
{
    /// <summary>Applies authoritative workspace journal freshness to retained check receipts.</summary>
    public RunVerificationEvidence ApplyWorkspaceFreshness(
        IReadOnlyDictionary<DateTimeOffset, WorkspaceCheckFreshness> freshnessByCheckStart)
    {
        ArgumentNullException.ThrowIfNull(freshnessByCheckStart);
        var checks = Checks.Select(check =>
        {
            if (!RequiredForCompletion(check)
                || check.Status is not (VerificationCheckStatus.Passed or VerificationCheckStatus.Pending))
            {
                return check;
            }

            WorkspaceCheckFreshness freshness = default;
            bool hasFreshness = check.ExecutionStartedAtUtc is { } startedAt
                && freshnessByCheckStart.TryGetValue(startedAt.ToUniversalTime(), out freshness);
            bool unknown = !hasFreshness || !freshness.IsKnown;
            bool changedAfterStart = hasFreshness && freshness.ChangedAfterStart;
            if (!unknown && !changedAfterStart) return check;

            if (check.Status == VerificationCheckStatus.Pending)
                return check with { InvalidatedDuringExecution = true };

            return check with
            {
                Status = VerificationCheckStatus.Stale,
                Code = unknown ? "verification_freshness_unknown" : "verification_stale",
            };
        }).ToArray();
        return new RunVerificationEvidence(checks, new HashSet<string>(_expectedFailures, StringComparer.Ordinal),
            Overflowed, HasMutations, _programFailure);
    }

    private static bool RequiredForCompletion(VerificationCheckEvidence check) =>
        check.IsVerification || check.ExpectedExitCode != 0;
}
