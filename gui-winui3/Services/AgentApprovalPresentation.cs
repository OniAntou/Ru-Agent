using System;
using System.Linq;
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
    /// asks, so the folder affordance must stay hidden for every other reason.
    /// </summary>
    public static bool CanAllowSessionDirectory(string? askReason) =>
        string.Equals(
            askReason?.Trim(),
            AgentApprovalSummaryPresentation.ExternalDirectoryRequired,
            StringComparison.Ordinal);

    /// <summary>
    /// Ask reasons that are shown with dedicated copy but must never become a
    /// reusable project rule. A protected path stays one-time: remembering it
    /// would turn one approval into standing access to a credential directory.
    /// An explicit ask rule stays one-time because an allow can never outrank
    /// it, so a remembered rule would be dead weight that reads as an override.
    /// </summary>
    private static readonly IReadOnlyList<string> OneTimeOnlyReasons =
    [
        AgentApprovalSummaryPresentation.TerminalProtectedPathRequiresApproval,
        AgentApprovalSummaryPresentation.RuleAsks,
    ];

    /// <summary>
    /// Whether an allow is remembered for the rest of the chat session, as the
    /// approve button's label promises. An explicit ask rule (`rule_asks`,
    /// which outranks the remembered allow) keeps the single-use label; every
    /// other reason keeps the session memory the ledger provides.
    /// </summary>
    public static bool CanRememberForSession(string? askReason)
    {
        var trimmed = askReason?.Trim();
        return !string.Equals(
            trimmed,
            AgentApprovalSummaryPresentation.RuleAsks,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the pending approval may be remembered as a project rule. The
    /// server honours allow_always for known ask reasons that are not
    /// one-time-only: an external-directory ask saves the approved folder as a
    /// bounded path pattern, every other reason saves the reviewed action
    /// pattern. Unknown reasons and one-time-only reasons stay one-time.
    /// </summary>
    public static bool CanRememberAllowAlways(string? askReason)
    {
        var trimmed = askReason?.Trim();
        return !string.IsNullOrWhiteSpace(trimmed)
            && !OneTimeOnlyReasons.Contains(trimmed, StringComparer.Ordinal)
            && AgentApprovalSummaryPresentation.MappedReasonCodes.Contains(trimmed, StringComparer.Ordinal);
    }

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
