namespace RuAgent.Harness.Core.Context;

public sealed partial class RunVerificationEvidence
{
    public string? DescribeExitMismatch()
    {
        var mismatches = Checks.Where(IsConfirmedExitMismatch).ToArray();
        if (mismatches.Length == 0) return null;
        var other = OutstandingChecks().Where(check => !IsConfirmedExitMismatch(check)).ToArray();
        return "The run could not complete: a declared negative test returned a different exit code.\n\n"
            + string.Join("\n", mismatches.Take(8).Select(check =>
                $"- {check.Command.Replace('\r', ' ').Replace('\n', ' ')}: expected {check.ExpectedExitCode}, observed {check.ObservedExitCode}."))
            + (mismatches.Length > 8 ? $"\n- {mismatches.Length - 8} additional mismatches." : "")
            + (other.Length > 0 ? "\n\nOther outstanding checks: "
                + string.Join(", ", other.Take(8).Select(check => check.Code))
                + (other.Length > 8 ? $"; {other.Length - 8} more" : "") + "." : "")
            + (Overflowed ? "\nRequired verification capacity was exceeded." : "")
            + (_programFailure is not null ? "\nProgram effects are not fully recorded; do not repeat uncertain effects." : "")
            + "\n\nThese checks remain failed. A successful alternative invocation does not change the original result. "
            + "For a native executable's exact exit code, use run_process; a PowerShell wrapper can return 1 instead. "
            + "No automatic retry was scheduled for this final report.";
    }

    public bool HasExitMismatch => Checks.Any(IsConfirmedExitMismatch);

    private static bool IsConfirmedExitMismatch(VerificationCheckEvidence check) => check.ExpectedExitCode != 0
        && check.Status == VerificationCheckStatus.Failed && check.Code == "expected_exit_mismatch"
        && check.ObservedExitCode is not null;

    public string? DescribePending()
    {
        if (_programFailure is not null) return _programFailure + ": program effects are not fully recorded. Inspect the workspace; do not repeat uncertain effects. Report [BLOCKED].";
        var pending = OutstandingChecks().ToArray();
        if (pending.Length == 0 && !Overflowed) return null;
        return "Outstanding declared checks: " + string.Join(", ", pending.Take(8)
            .Select(VerificationEvidenceGuidance.Describe))
            + (pending.Length > 8 ? $"; {pending.Length - 8} more" : "")
            + (Overflowed ? "; verification_tracking_limit" : "")
            + (pending.Any(CanBeSuperseded)
                ? ". For a retired check that previously passed (for example a removed temporary test), run an appropriate final verification with supersedes=[the stale check ID shown above]. An unambiguous provider call ID is also accepted. Only a fresh passing verification in the same canonical directory can replace it; failed, pending and negative checks cannot be replaced"
                : "")
            + (pending.Any(check => check.RejectedBeforeLaunch)
                ? ". A check rejected before launch never ran; a corrected check of the same kind that passes resolves it"
                : "")
            + (pending.Any(check => check.InvocationInvalid)
                ? ". A Python verification invocation was invalid (direct inline parse error or empty pytest discovery). Correct it and pass a meaningful verification with the same Python interpreter and working directory; this exception never clears a test failure or expected-exit mismatch"
                : "")
            + (pending.Any(check => check.Code == "expected_exit_mismatch")
                ? ". An expected-exit mismatch is an observed result, not a transient tool error. Do not keep rerunning a deliberately failing command to force its expected code. A different invocation does not clear the original check. Report the actual exit and use [BLOCKED] for the incomplete outcome."
                : pending.Any(check => check.InvocationInvalid)
                    ? ". Inspecting logs alone does not resolve the invalid probe. Report [BLOCKED] if it cannot be corrected."
                    : ". Inspecting logs does not satisfy a check. Rerun outstanding checks after repairs, or explicitly replace eligible stale checks as described above; otherwise report [BLOCKED].");
    }

}
