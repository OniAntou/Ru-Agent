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
            item.SetDetailRepeated(false);
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
                || current.State != AgentActivityState.Completed
                || previous.State != AgentActivityState.Completed
                || !previous.IsVisibleInFeed)
            {
                continue;
            }

            // Thinking rows never collapse -- their duration is the only report
            // of where the step's time went -- but a model that reuses one
            // deliberation across steps used to print that same paragraph once
            // per step. The row stays, with its own duration; only the repeat is
            // dropped, and the value stays on the item.
            if (current.Kind == AgentActivityKind.Thinking)
            {
                if (previous.Kind == AgentActivityKind.Thinking
                    && string.Equals(current.Detail, previous.Detail, StringComparison.Ordinal))
                {
                    current.SetDetailRepeated(true);
                }

                continue;
            }

            // The key covers everything the row renders (kind, target, counts,
            // deltas, detail preview, safe code). DurationMs is excluded on
            // purpose: only Thinking renders a duration and Thinking never
            // collapses, so two otherwise identical rows read exactly the same.
            if (current.Kind == previous.Kind
                && string.Equals(current.FileName, previous.FileName, StringComparison.Ordinal)
                && string.Equals(current.Symbol, previous.Symbol, StringComparison.Ordinal)
                && string.Equals(current.ToolDisplay, previous.ToolDisplay, StringComparison.Ordinal)
                && Nullable.Equals(current.Count, previous.Count)
                && Nullable.Equals(current.Added, previous.Added)
                && Nullable.Equals(current.Removed, previous.Removed)
                && string.Equals(current.Detail, previous.Detail, StringComparison.Ordinal)
                && string.Equals(current.SafeCode, previous.SafeCode, StringComparison.Ordinal))
            {
                // Fold the hidden row's duration into the survivor so the total
                // time is preserved instead of silently dropped.
                current.CollapsedDurationMs += previous.EffectiveDurationMs ?? 0;
                previous.IsVisibleInFeed = false;
            }
        }
    }
}
