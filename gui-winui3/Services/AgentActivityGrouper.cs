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

        var visible = new bool[items.Count];
        var durations = new int[items.Count];
        var repeated = new bool[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            // Narration is the model's own sentence, and the same sentence
            // streams into the prose beside it: showing the row too prints
            // every narration twice. The prose keeps the words.
            visible[i] = items[i].Kind != AgentActivityKind.Narration;
        }

        CollapseIdenticalCompleted(items, visible, durations, repeated);
        // Publish only final derived values. Resetting bindings first briefly
        // revealed collapsed rows/details on every unrelated feed update.
        for (int i = 0; i < items.Count; i++)
        {
            items[i].IsVisibleInFeed = visible[i];
            items[i].CollapsedDurationMs = durations[i];
            items[i].SetDetailRepeated(repeated[i]);
        }
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

    private static void CollapseIdenticalCompleted(
        IList<AgentActivityItem> items, bool[] visible, int[] durations, bool[] repeated)
    {
        for (int i = 1; i < items.Count; i++)
        {
            var previous = items[i - 1];
            var current = items[i];
            if (!visible[i]
                || current.State != AgentActivityState.Completed
                || previous.State != AgentActivityState.Completed
                || !visible[i - 1])
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
                    repeated[i] = true;
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
                durations[i] += (previous.DurationMs ?? 0) + durations[i - 1];
                visible[i - 1] = false;
            }
        }
    }
}
