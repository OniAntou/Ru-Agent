using RuBackend.Agent;

namespace RuBackend.Conversation;

public sealed record ChatRequest(
    string Prompt,
    string? SessionId,
    string? CorrelationId = null,
    CapabilityProfile Profile = CapabilityProfile.Chat)
{
    public const int MaxPromptCharacters = 16_000;
    public const int MaxMessageBytes = 256 * 1024;

    public ChatRequest(string prompt, string? sessionId)
        : this(prompt, sessionId, null)
    {
    }
}
