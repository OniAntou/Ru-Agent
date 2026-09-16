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
