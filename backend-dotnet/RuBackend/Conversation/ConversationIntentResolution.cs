namespace RuBackend.Conversation;

public enum ConversationIntentKind
{
    NoMatch,
    Greeting,
    Acknowledgement,
    Correction,
    PathTask,
    Search,
    CurrentFact,
}

public sealed record ConversationIntentResolution(
    ConversationIntentKind Kind,
    double Confidence,
    string? LocalReply = null,
    string? ModelInstruction = null)
{
    public bool IsLocalReply => !string.IsNullOrWhiteSpace(LocalReply) && Confidence >= 0.95;

    public bool RequiresModelContext => !IsLocalReply;
}

public interface IConversationIntentResolver
{
    ConversationIntentResolution Resolve(string prompt);
}
