using System;
using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// Presentation decisions for the inline Agent approval panel. Kept out of the
/// chat code-behind so "which command does the user get asked to approve" is
/// assertable without a window.
/// </summary>
public static class AgentApprovalPresentation
{
    /// <summary>
    /// Whether the pending approval may be granted for the whole chat session.
    /// The server only honours allow_session_directory for external-directory
    /// asks, so the Always affordance must stay hidden for every other reason.
    /// </summary>
    public static bool CanAllowSessionDirectory(string? askReason) =>
        string.Equals(
            askReason?.Trim(),
            AgentApprovalSummaryPresentation.ExternalDirectoryRequired,
            StringComparison.Ordinal);

    /// <summary>
    /// The terminal command to show in the approval panel. The controller's own
    /// proposal is authoritative; the activity feed is only a fallback for a run
    /// reattached mid-flight, where the proposal arrived before this client
    /// subscribed. The newest terminal row with detail wins.
    /// </summary>
    public static string? ResolveTerminalCommand(
        string? pendingApprovalCommand,
        IEnumerable<AgentActivityItem> activities)
    {
        ArgumentNullException.ThrowIfNull(activities);
        if (!string.IsNullOrWhiteSpace(pendingApprovalCommand))
        {
            return pendingApprovalCommand;
        }

        string? fallback = null;
        foreach (var activity in activities)
        {
            if (activity.Kind == AgentActivityKind.Terminal
                && !string.IsNullOrWhiteSpace(activity.Detail))
            {
                fallback = activity.Detail;
            }
        }

        return fallback;
    }
}
