namespace RuAgent.Harness.Core.Prompting;

public sealed record ModelStepResult
{
    public ModelStepResult(AssistantStep step, SystemPromptReceipt promptReceipt, ModelUsage? usage = null, string? reasoningText = null, ProviderContinuation? providerContinuation = null)
    {
        Step = step ?? throw new ArgumentNullException(nameof(step));
        PromptReceipt = promptReceipt ?? throw new ArgumentNullException(nameof(promptReceipt));
        Usage = usage;
        ReasoningText = reasoningText;
        ProviderContinuation = providerContinuation;
    }

    public AssistantStep Step { get; }

    public SystemPromptReceipt PromptReceipt { get; }

    public ModelUsage? Usage { get; }

    /// <summary>Bounded provider reasoning excerpt for activity display.
    /// Observational only, never fed back into prompts.</summary>
    public string? ReasoningText { get; }

    public ProviderContinuation? ProviderContinuation { get; }
}
