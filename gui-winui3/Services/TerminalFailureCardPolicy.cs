namespace RuApp.Services;

/// <summary>
/// The terminal failure card is UI-only copy: it is never persisted into the
/// session history, so a transcript reload (which rebuilds from history) drops
/// it. This policy decides when that card must be re-appended after a reload
/// and composes the actionable failure copy.
/// </summary>
internal static class TerminalFailureCardPolicy
{
    internal static bool ShouldAppend(
        string? runStatus,
        bool hasRenderedReply,
        bool hasExistingErrorCard) =>
        (string.Equals(runStatus, "failed", StringComparison.Ordinal)
            || string.Equals(runStatus, "interrupted", StringComparison.Ordinal))
        && !hasRenderedReply
        && !hasExistingErrorCard;

    internal static string Compose(string? failureReason, bool canContinue)
    {
        string rendered = AgentFailurePresentation.Render(failureReason);
        string copy = string.IsNullOrWhiteSpace(rendered) ? "Coding Agent failed" : rendered;
        var content = new System.Text.StringBuilder();
        content.Append("**").Append(copy).Append("**");
        if (canContinue)
        {
            content.Append('\n').Append("Type **Continue** to resume.");
        }

        return content.ToString();
    }
}
