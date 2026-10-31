namespace RuAgent.Harness.Core.Context;

/// <summary>Bounded descriptions of obligations, never inferred test equivalence.</summary>
internal static class VerificationEvidenceGuidance
{
    public static string Describe(VerificationCheckEvidence check)
    {
        var action = check.Code == "expected_exit_mismatch"
            ? "report the observed exit mismatch; do not force the expected code"
            : check.Status == VerificationCheckStatus.Pending && check.ExecutionId is { } execution
                ? $"read terminal {execution} until exit; do not start a duplicate"
                : "repair and rerun this check";
        return $"{OneLine(check.ProviderCallId, 128)} ({check.Code})"
            + $" [cwd: {OneLine(check.WorkingDirectory ?? "unavailable", 512)}]: {OneLine(check.Command, 1024)}"
            + $" [action: {action}]";
    }

    private static string OneLine(string value, int maximum)
    {
        var length = Math.Min(value.Length, maximum);
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return new string(value.AsSpan(0, length).ToArray().Select(c => char.IsControl(c) ? ' ' : c).ToArray())
            + (length < value.Length ? "…" : "");
    }
}
