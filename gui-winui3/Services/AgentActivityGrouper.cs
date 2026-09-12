using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// Feed presentation policy applied in place after every activity mutation:
/// - Completed Thinking rows never reach the feed (duration-only noise).
/// - Consecutive identical completed rows (same kind and target) keep the
///   last one.
/// Running, waiting and failed rows are never collapsed.
/// </summary>
internal static class AgentActivityGrouper
{
    public static void Reconcile(IList<AgentActivityItem> items)
    {
        if (items is null)
        {
            return;
        }

        foreach (var item in items)
        {
            item.IsVisibleInFeed = true;
            item.CollapsedDurationMs = 0;
        }

        HideCompletedThoughts(items);
        CollapseIdenticalCompleted(items);
    }

    private static void HideCompletedThoughts(IList<AgentActivityItem> items)
    {
        foreach (var item in items)
        {
            if (item.Kind == AgentActivityKind.Thinking
                && item.State == AgentActivityState.Completed)
            {
                item.IsVisibleInFeed = false;
            }
        }
    }

    private static void CollapseIdenticalCompleted(IList<AgentActivityItem> items)
    {
        for (int i = 1; i < items.Count; i++)
        {
            var previous = items[i - 1];
            var current = items[i];
            if (!current.IsVisibleInFeed
                || current.Kind == AgentActivityKind.Thinking
                || current.State != AgentActivityState.Completed
                || previous.State != AgentActivityState.Completed
                || !previous.IsVisibleInFeed)
            {
                continue;
            }

            if (current.Kind == previous.Kind
                && string.Equals(current.FileName, previous.FileName, StringComparison.Ordinal)
                && string.Equals(current.Symbol, previous.Symbol, StringComparison.Ordinal)
                && string.Equals(current.ToolDisplay, previous.ToolDisplay, StringComparison.Ordinal))
            {
                previous.IsVisibleInFeed = false;
            }
        }
    }
}
