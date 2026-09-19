namespace RuApp.Services;

/// <summary>How loaded the model context is, and therefore how the meter is painted.</summary>
public enum AgentContextMeterSeverity
{
    Normal,
    Warning,
    Danger,
}

/// <summary>One reading of the context meter.</summary>
public sealed record AgentContextMeterState(
    int MaximumTokens,
    string DisplayLimit,
    int EstimatedTokens,
    int ReportedTokens,
    int UsedTokens,
    double Percentage,
    AgentContextMeterSeverity Severity)
{
    /// <summary>No model budget is known, so the meter shows a count instead of a share.</summary>
    public bool HasBudget => MaximumTokens > 0;

    /// <summary>The percent label is only meaningful against a known budget.</summary>
    public bool ShowPercentage => HasBudget;
}

/// <summary>
/// The context-window meter above the composer.
///
/// The arithmetic used to be inline in ChatView.UpdateContextUsage together with the
/// brush lookups. Splitting the numbers out makes "which figure is authoritative"
/// and "when does the meter turn red" assertable; the view only paints the result.
/// </summary>
public static class AgentContextMeterPolicy
{
    /// <summary>At or above this share the meter is painted as a warning.</summary>
    public const double WarningThresholdPercent = 70.0;

    /// <summary>At or above this share the meter is painted as danger.</summary>
    public const double DangerThresholdPercent = 90.0;

    public static AgentContextMeterSeverity Severity(double percentage) =>
        percentage > DangerThresholdPercent
            ? AgentContextMeterSeverity.Danger
            : percentage > WarningThresholdPercent
                ? AgentContextMeterSeverity.Warning
                : AgentContextMeterSeverity.Normal;

    /// <summary>
    /// One reading from the two independent sources: the character estimate for what
    /// is about to be sent, and the provider's own report for what the last step
    /// actually cost. The provider figure already includes system prompt, tool
    /// definitions and history, so it wins whenever it is larger; the estimate only
    /// covers the case where no step has run yet.
    /// </summary>
    public static AgentContextMeterState Evaluate(
        int maximumTokens,
        string displayLimit,
        int estimatedTokens,
        int reportedTokens)
    {
        ArgumentNullException.ThrowIfNull(displayLimit);

        int usedTokens = System.Math.Max(estimatedTokens, reportedTokens);
        double percentage = maximumTokens > 0
            ? System.Math.Min(100.0, usedTokens * 100.0 / maximumTokens)
            : 0.0;

        return new AgentContextMeterState(
            maximumTokens,
            displayLimit,
            estimatedTokens,
            reportedTokens,
            usedTokens,
            percentage,
            Severity(percentage));
    }

    /// <summary>
    /// System prompt plus tool definitions, inferred only when the provider has
    /// reported a real step: the report covers system, tools and history while
    /// the estimate covers history alone, so the gap between them is the
    /// overhead. Null while no step has run, so the view hides the legend row
    /// instead of guessing a number.
    /// </summary>
    public static int? OverheadTokens(int estimatedTokens, int reportedTokens) =>
        reportedTokens > estimatedTokens ? reportedTokens - estimatedTokens : null;

    /// <summary>
    /// Largest provider-reported token total across the transcript and the active
    /// step. The provider figure covers one step, so a longer conversation is the
    /// largest of them rather than a sum; a later, smaller report must not lower the
    /// meter.
    /// </summary>
    public static int MaxReportedTokens(IEnumerable<int> reportedTotals)
    {
        ArgumentNullException.ThrowIfNull(reportedTotals);

        int maximum = 0;
        foreach (var total in reportedTotals)
        {
            if (total > maximum)
            {
                maximum = total;
            }
        }

        return maximum;
    }

    /// <summary>Characters and images the meter counts from the transcript and the draft.</summary>
    public static (int Characters, int Images) Measure(
        IEnumerable<(string? Content, int AttachmentCount)> messages,
        string? draftText = null,
        int pendingAttachments = 0)
    {
        ArgumentNullException.ThrowIfNull(messages);

        int characters = 0;
        int images = 0;
        foreach (var (content, attachmentCount) in messages)
        {
            if (!string.IsNullOrEmpty(content))
            {
                characters += content.Length;
            }

            images += attachmentCount;
        }

        if (!string.IsNullOrEmpty(draftText))
        {
            characters += draftText.Length;
        }

        return (characters, images + pendingAttachments);
    }
}
