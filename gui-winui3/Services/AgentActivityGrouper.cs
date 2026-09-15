using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// Feed presentation policy applied in place after every activity mutation:
/// - Thinking rows always stay in the feed (running shows "Thinking", settled
///   shows "Thought for Xs"): hiding them left the assistant bubble empty
///   while the model worked, unlike Codex/OpenCode.
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

        CollapseIdenticalCompleted(items);
    }

    /// <summary>
    /// Settles rows still in the running state when the run ends: an activity
    /// frame can lose the race with the run's terminal transition, and a row left
    /// running would keep its progress affordance forever.
    /// </summary>
    public static void SettleRunningRows(IList<AgentActivityItem> items)
    {
        if (items is null)
        {
            return;
        }

        foreach (var item in items)
        {
            item.SettleInterruptedRun();
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
