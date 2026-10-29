namespace RuAgent.Harness.Core.Context;

/// <summary>Pure completion rules shared by evidence projection and guidance.</summary>
internal static class VerificationObligationPolicy
{
    private static readonly HashSet<string> DocumentationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".rst", ".adoc",
    };

    /// <summary>Extension-only: documentation targets never make a source edit awaiting verification.</summary>
    public static bool IsDocumentationPath(string path) => DocumentationExtensions.Contains(Path.GetExtension(path));

    public static bool IsOutstanding(VerificationCheckEvidence check, IReadOnlySet<string> replaced) =>
        (check.IsVerification || check.ExpectedExitCode != 0)
        && check.Status != VerificationCheckStatus.Passed
        && !(CanBeSuperseded(check) && replaced.Contains(check.Identity));

    public static bool CanBeSuperseded(VerificationCheckEvidence check) => check.IsVerification
        && check.ExpectedExitCode == 0 && check.ObservedExitCode == 0
        && check.Status == VerificationCheckStatus.Stale && check.Code == "verification_stale"
        && !check.InvalidatedDuringExecution && check.WorkingDirectoryIdentity is not null;
}
