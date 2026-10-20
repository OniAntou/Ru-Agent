using System;
using System.Collections.Generic;
using RuAgent.Client;

namespace RuApp.Services;

/// <summary>
/// Whether a just-rendered Agent reply may stay on screen after the session history
/// is reloaded, and whether it can be shown at all.
///
/// The reload races conversation-outbox delivery: the history snapshot can still
/// contain only the user turn, and replacing the transcript then would make the reply
/// the user just watched stream disappear.
/// </summary>
public static class ChatHistoryReloadPolicy
{
    /// <summary>
    /// Whether the streamed reply should be kept instead of the reloaded history.
    /// Returns false when there is nothing showable to keep, which includes the case
    /// where the text is a machine tool envelope: that is persisted for the model and
    /// must never be rendered as an assistant answer.
    /// </summary>
    public static bool ShouldPreserveStreamedReply(
        IReadOnlyList<(string Role, string Content)> history,
        string? streamedReply)
    {
        ArgumentNullException.ThrowIfNull(history);

        // Nothing was rendered, or what was rendered is not a reply the user should
        // keep looking at. The caller used to re-check both of these itself, which
        // meant the decision lived half here and half at the call site.
        if (string.IsNullOrWhiteSpace(streamedReply)
            || AgentActivityDisplayPolicy.LooksLikeToolSelection(streamedReply))
        {
            return false;
        }

        // An empty snapshot is delivery lag, not a history that lost the reply.
        if (history.Count == 0)
        {
            return true;
        }

        // Keep the streamed text unless the snapshot already ends with the very same
        // assistant turn, in which case the reloaded copy is authoritative and
        // cheaper to render.
        var (lastRole, lastContent) = history[^1];
        return !string.Equals(lastRole, "assistant", StringComparison.Ordinal)
            || !string.Equals(lastContent, streamedReply, StringComparison.Ordinal);
    }
}
