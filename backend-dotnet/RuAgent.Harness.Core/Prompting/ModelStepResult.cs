namespace RuAgent.Harness.Core.Prompting;

public sealed record ModelStepResult
{
    public ModelStepResult(AssistantStep step, SystemPromptReceipt promptReceipt, ModelUsage? usage = null, string? reasoningText = null,
        ProviderContinuation? providerContinuation = null, int providerAttempts = 1, string effectiveEffort = "unknown",
        string selectedEffort = "unknown", string? lastProviderRetryCode = null)
    {
        Step = step ?? throw new ArgumentNullException(nameof(step));
        PromptReceipt = promptReceipt ?? throw new ArgumentNullException(nameof(promptReceipt));
        Usage = usage;
        ReasoningText = reasoningText;
        ProviderContinuation = providerContinuation;
        ProviderAttempts = Math.Clamp(providerAttempts, 1, 5);
        EffectiveEffort = effectiveEffort is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" ? effectiveEffort : "unknown";
        SelectedEffort = selectedEffort is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" ? selectedEffort : "unknown";
        LastProviderRetryCode = IsSafeRetryCode(lastProviderRetryCode) ? lastProviderRetryCode : null;
    }

    public AssistantStep Step { get; }

    public SystemPromptReceipt PromptReceipt { get; }

    public ModelUsage? Usage { get; }

    /// <summary>Bounded provider reasoning excerpt for activity display.
    /// Observational only, never fed back into prompts.</summary>
    public string? ReasoningText { get; }

    public ProviderContinuation? ProviderContinuation { get; }

    /// <summary>Number of HTTP generation attempts used by this logical model step.</summary>
    public int ProviderAttempts { get; }

    /// <summary>
    /// The bounded failure that made the previous provider attempt retry, for the
    /// activity row. Null when no provider retry happened.
    /// </summary>
    public string? LastProviderRetryCode { get; }

    /// <summary>Bounded reasoning effort sent on the successful provider attempt.</summary>
    public string EffectiveEffort { get; }

    public string SelectedEffort { get; }

    private static bool IsSafeRetryCode(string? code) =>
        code is { Length: >= 1 and <= 64 }
        && code[0] is >= 'a' and <= 'z'
        && code.All(character => char.IsAsciiLetterLower(character)
            || char.IsAsciiDigit(character)
            || character is '_' or '.');
}
