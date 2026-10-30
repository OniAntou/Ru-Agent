namespace RuAgent.Harness.Core.Context;

/// <summary>Pure obligation rules shared by evidence projection and guidance.</summary>
internal static class VerificationObligationPolicy
{
    private static readonly HashSet<string> DocumentationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".rst", ".adoc",
    };

    /// <summary>Extension-only: documentation targets never make a source edit awaiting verification.</summary>
    public static bool IsDocumentationPath(string path) => DocumentationExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// A declared check is outstanding while its latest result is failed or pending. Only the same
    /// identity passing retires it (ADR 0039 decision 2).
    /// </summary>
    public static bool IsOutstanding(VerificationCheckEvidence check) =>
        (check.IsVerification || check.ExpectedExitCode != 0)
        && check.Status != VerificationCheckStatus.Passed;
}
