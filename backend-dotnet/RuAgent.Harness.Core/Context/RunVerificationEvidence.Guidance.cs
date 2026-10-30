namespace RuAgent.Harness.Core.Context;

public sealed partial class RunVerificationEvidence
{
    public string? DescribePending()
    {
        if (_programFailure is not null) return _programFailure + ": program effects are not fully recorded. Inspect the workspace; do not repeat uncertain effects. Report [BLOCKED].";
        var pending = OutstandingChecks().ToArray();
        if (pending.Length == 0 && !Overflowed) return null;
        return "Outstanding declared checks: " + string.Join(", ", pending.Take(8)
            .Select(VerificationEvidenceGuidance.Describe))
            + (pending.Length > 8 ? $"; {pending.Length - 8} more" : "")
            + (Overflowed ? "; verification_tracking_limit" : "")
            + (pending.Any(check => check.Code == "expected_exit_mismatch")
                ? ". An expected-exit mismatch is an observed result, not a transient tool error. Do not keep rerunning a deliberately failing command to force its expected code. A different invocation does not clear the original check. Report the actual exit and use [BLOCKED] for the incomplete outcome."
                : ". Inspecting logs does not satisfy a check. Rerun outstanding checks after repairs; otherwise report [BLOCKED].");
    }
}
