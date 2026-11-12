namespace RuAgent.Runtime.Adapters.Models;

/// <summary>Memory ceilings, independent of the pinned provider's token budget.</summary>
public static class ModelInputLimits
{
    // Raised from 1,024 messages / 8 MiB so a long run on a large-context model keeps more
    // verbatim history between compactions; the run compacts at 75% of this window, and the
    // 64 MiB request bound below still caps one request's memory.
    public const int MaxMessages = 4096;
    public const int MaxHistoryBytes = 32 * 1024 * 1024;
    public const int MaxPromptChars = 1_000_000;
    public const int MaxSystemPromptChars = 8 * 1024 * 1024;
    public const int MaxToolResultChars = 128 * 1024;
    public const int MaxRequestBytes = 64 * 1024 * 1024;
}
