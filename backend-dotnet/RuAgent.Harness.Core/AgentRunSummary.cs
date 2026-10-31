using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Core;

/// <summary>
/// Safe terminal summary data. This Core type deliberately contains no raw path,
/// command, output, prompt, diff or provider metadata.
/// </summary>
public sealed record AgentRunSummary
{
    public const int MaximumWorkspaceLabelLength = 128;
    public const int MaximumCount = 1_000_000;

    public AgentRunSummary(
        string status,
        string verificationState,
        string? workspaceLabel = null,
        int? filesScanned = null,
        int? filesChanged = null,
        int? filesCreated = null,
        int? filesDeleted = null)
    {
        if (status is not ("succeeded" or "failed" or "cancelled" or "interrupted"))
        {
            throw new ArgumentException("Summary status is invalid.", nameof(status));
        }

        if (verificationState is not ("verified" or "unverified" or "not_applicable"))
        {
            throw new ArgumentException("Summary verification state is invalid.", nameof(verificationState));
        }

        ValidateWorkspaceLabel(workspaceLabel);
        ValidateCount(filesScanned, nameof(filesScanned));
        ValidateCount(filesChanged, nameof(filesChanged));
        ValidateCount(filesCreated, nameof(filesCreated));
        ValidateCount(filesDeleted, nameof(filesDeleted));

        Status = status;
        VerificationState = verificationState;
        WorkspaceLabel = workspaceLabel;
        FilesScanned = filesScanned;
        FilesChanged = filesChanged;
        FilesCreated = filesCreated;
        FilesDeleted = filesDeleted;
    }

    public string Status { get; }
    public string VerificationState { get; }
    public string? WorkspaceLabel { get; }
    public int? FilesScanned { get; }
    public int? FilesChanged { get; }
    public int? FilesCreated { get; }
    public int? FilesDeleted { get; }

    public static AgentRunSummary FromTerminalRun(
        AgentRun run,
        string? workspaceLabel,
        AgentRunSummaryMetrics? metrics = null,
        RunVerificationEvidence? verificationEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!run.State.IsTerminal())
        {
            throw new ArgumentException("Only terminal runs can have a summary.", nameof(run));
        }

        var status = run.State switch
        {
            HarnessRunState.Completed => "succeeded",
            HarnessRunState.Failed => "failed",
            HarnessRunState.Cancelled => "cancelled",
            HarnessRunState.Interrupted => "interrupted",
            _ => throw new ArgumentOutOfRangeException(nameof(run)),
        };
        var safeMetrics = metrics ?? AgentRunSummaryMetrics.Empty;
        var verification = verificationEvidence is null
            ? VerificationStateWithoutEvidence(safeMetrics)
            : VerificationStateFromEvidence(verificationEvidence);

        return new AgentRunSummary(
            status,
            verification,
            workspaceLabel,
            safeMetrics.FilesScanned,
            safeMetrics.FilesChanged,
            safeMetrics.FilesCreated,
            safeMetrics.FilesDeleted);
    }

    /// <summary>
    /// ADR 0039 item 7. verified: no outstanding declared check and a declared check launched after the
    /// last structured source edit. not_applicable: no structured source edit and no declared check launched.
    /// Otherwise unverified. Computer input and other activity never change this label.
    /// </summary>
    private static string VerificationStateFromEvidence(RunVerificationEvidence evidence)
    {
        if (evidence.BlockingCode is null && evidence.DeclaredCheckSinceSourceEdit)
        {
            return "verified";
        }

        return !evidence.HasSourceEdits && !evidence.DeclaredCheckSinceSourceEdit ? "not_applicable" : "unverified";
    }

    // Without run history, activity-level mutations stay conservatively unverified. Nothing can certify a check.
    private static string VerificationStateWithoutEvidence(AgentRunSummaryMetrics metrics) =>
        metrics.HasVerifiedMutation || metrics.HasUnverifiedMutation ? "unverified" : "not_applicable";

    private static void ValidateWorkspaceLabel(string? label)
    {
        if (label is null)
        {
            return;
        }

        if (label.Length is < 1 or > MaximumWorkspaceLabelLength
            || string.IsNullOrWhiteSpace(label)
            || label is "." or ".."
            || label.EndsWith(".", StringComparison.Ordinal)
            || label.EndsWith(" ", StringComparison.Ordinal)
            || label.Any(char.IsControl)
            || label.Contains('/', StringComparison.Ordinal)
            || label.Contains('\\', StringComparison.Ordinal)
            || label.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("Workspace label must be a safe display label.", nameof(label));
        }
    }

    private static void ValidateCount(int? value, string parameterName)
    {
        if (value is < 0 or > MaximumCount)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

/// <summary>Legacy activity metrics confirm tool execution, never test acceptance.</summary>
public sealed record AgentRunSummaryMetrics(
    int? FilesScanned,
    int? FilesChanged,
    int? FilesCreated,
    int? FilesDeleted,
    bool HasVerifiedMutation,
    bool HasUnverifiedMutation)
{
    public static AgentRunSummaryMetrics Empty { get; } = new(null, null, null, null, false, false);
}
