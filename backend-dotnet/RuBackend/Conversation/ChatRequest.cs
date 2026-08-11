namespace RuBackend.Conversation;

public sealed record ChatRequest(
    string Prompt,
    string? SessionId)
{
    public const int MaxPromptCharacters = 16_000;
    public const int MaxMessageBytes = 256 * 1024;
}
