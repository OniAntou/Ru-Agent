namespace RuAgent.Harness.Core.Prompting;

public sealed record ModelStepResult
{
    public ModelStepResult(AssistantStep step, SystemPromptReceipt promptReceipt)
    {
        Step = step ?? throw new ArgumentNullException(nameof(step));
        PromptReceipt = promptReceipt ?? throw new ArgumentNullException(nameof(promptReceipt));
    }

    public AssistantStep Step { get; }

    public SystemPromptReceipt PromptReceipt { get; }
}
