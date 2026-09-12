using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// Feed presentation policy applied in place after every activity mutation:
/// - Consecutive completed Thinking rows fold into the last one (durations
///   summed) so thought spam becomes a single "Thought for Xs" row.
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

        CollapseThinkingChains(items);
        CollapseIdenticalCompleted(items);
    }

    private static void CollapseThinkingChains(IList<AgentActivityItem> items)
    {
        int runStart = -1;
        for (int i = 0; i <= items.Count; i++)
        {
            bool inRun = i < items.Count
                && items[i].Kind == AgentActivityKind.Thinking
                && items[i].State == AgentActivityState.Completed;

            if (inRun && runStart < 0)
            {
                runStart = i;
            }

            if (!inRun && runStart >= 0)
            {
                if (i - runStart >= 2)
                {
                    int folded = 0;
                    for (int j = runStart; j < i - 1; j++)
                    {
                        folded += items[j].DurationMs ?? 0;
                        items[j].IsVisibleInFeed = false;
                    }

                    items[i - 1].CollapsedDurationMs = folded;
                }

                runStart = -1;
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
