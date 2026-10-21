namespace RuApp.Services;

/// <summary>
/// What a waiting activity row says. The row used to read "Waiting for confirmation"
/// beside the raw code (<c>input_required</c>, <c>terminal_external_cwd_requires_approval</c>),
/// which named the machine state instead of telling the user what the run needs from them.
/// </summary>
/// <remarks>
/// A code this table explains moves from the row to its tooltip: it stays reachable for a
/// bug report, which is what named codes are for. An unknown code stays on the row, so a
/// new wait reason is visible instead of being hidden behind a generic headline.
/// </remarks>
public static class AgentWaitPresentation
{
    public const string AnswerHeadline = "Waiting for your answer";
    public const string ApprovalHeadline = "Waiting for your approval";
    public const string TrustHeadline = "Waiting for workspace trust";

    public static string Headline(bool isStatusRow, string? safeCode) => Explain(safeCode)
        ?? (isStatusRow ? "Waiting for confirmation" : "Waiting to continue");

    /// <summary>Whether the headline already says what the code means.</summary>
    public static bool IsExplained(string? safeCode) => Explain(safeCode) is not null;

    private static string? Explain(string? safeCode)
    {
        string code = safeCode?.Trim() ?? string.Empty;
        if (code.Length == 0) return null;
        if (code is "input_required" or "waiting_input" or "waiting_user_input") return AnswerHeadline;
        if (code is "workspace_trust_required" or "waiting_workspace_trust") return TrustHeadline;
        if (code is "approval_required" or "waiting_approval"
            || AgentApprovalSummaryPresentation.MappedReasonCodes.Contains(code, StringComparer.Ordinal))
        {
            return ApprovalHeadline;
        }

        return null;
    }
}
