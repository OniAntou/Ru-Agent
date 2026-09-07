using System;
using System.Collections.Generic;

namespace RuApp.Services;

/// <summary>
/// Keeps a just-rendered Agent reply visible when the session history snapshot
/// races conversation-outbox delivery and still contains only the user turn.
/// </summary>
public static class ChatHistoryReloadPolicy
{
    public static bool ShouldPreserveStreamedReply(
        IReadOnlyList<(string Role, string Content)> history,
        string? streamedReply)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (string.IsNullOrWhiteSpace(streamedReply))
        {
            return false;
        }

        if (history.Count == 0)
        {
            return true;
        }

        var (lastRole, lastContent) = history[^1];
        return !string.Equals(lastRole, "assistant", StringComparison.Ordinal)
            || !string.Equals(lastContent, streamedReply, StringComparison.Ordinal);
    }
}
