namespace RuAgent.Runtime.Adapters.Models;

/// <summary>Memory ceilings, independent of the pinned provider's token budget.</summary>
public static class ModelInputLimits
{
    public const int MaxMessages = 1024;
    public const int MaxHistoryBytes = 8 * 1024 * 1024;
    public const int MaxPromptChars = 1_000_000;
    public const int MaxSystemPromptChars = 8 * 1024 * 1024;
    public const int MaxToolResultChars = 128 * 1024;
    public const int MaxRequestBytes = 64 * 1024 * 1024;
}
