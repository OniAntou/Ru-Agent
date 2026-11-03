namespace RuBackend.Business.Sessions;

internal static class SessionContextTurns
{
    internal static IReadOnlyList<ConversationTurn> Select(
        IReadOnlyList<SessionMessage> messages,
        int maxTurns)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (maxTurns <= 0)
        {
            return [];
        }

        string? pendingPrompt = null;
        string? pendingRunId = null;
        var pendingImageCount = 0;
        var turns = new List<ConversationTurn>();
        foreach (var message in messages)
        {
            var content = message.Content.Trim();
            if (content.Length == 0 && !(message.Role == "user" && message.SourceRunId is not null))
            {
                continue;
            }

            if (message.Role == "user")
            {
                pendingPrompt = content;
                pendingRunId = message.SourceRunId;
                pendingImageCount = message.ImageAttachmentCount;
            }
            else if (message.Role == "ai" && pendingPrompt is not null)
            {
                turns.Add(new ConversationTurn(pendingPrompt, content, pendingRunId, pendingImageCount));
                pendingPrompt = null;
                pendingRunId = null;
                pendingImageCount = 0;
            }
        }

        return turns.TakeLast(maxTurns).ToArray();
    }
}
