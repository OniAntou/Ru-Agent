using RuApp.Models;

namespace RuApp.Services;

/// <summary>One segment of the shelf's task-progress strip.</summary>
internal enum TodoSegmentState
{
    Pending,
    Active,
    Done,
}

/// <param name="AllDone">Every task is completed; the shelf keeps a completion summary instead of disappearing.</param>
/// <param name="HeaderPrimary">The first header line: the current task, the remaining count, or the completion sentence.</param>
/// <param name="HeaderSecondary">The second header line: the next task or the closing note.</param>
/// <param name="UseSegments">False for plans longer than <see cref="MaxSegments"/>; the view draws one continuous bar instead.</param>
/// <param name="BarFraction">Completed fraction for the continuous-bar fallback.</param>
internal sealed record AgentTodoPanelPresentation(
    bool IsVisible,
    bool AllDone,
    int Done,
    int Total,
    int Remaining,
    string Current,
    string HeaderPrimary,
    string HeaderSecondary,
    bool HasActive,
    double ListHeight,
    bool UseSegments,
    IReadOnlyList<TodoSegmentState> Segments,
    double BarFraction)
{
    /// <summary>Segments stay legible up to this many tasks; longer plans use the bounded bar.</summary>
    internal const int MaxSegments = 12;

    /// <summary>Bounded scroll viewport for the expanded list (about seven rows).</summary>
    internal const double MaxListHeight = 224;

    internal static AgentTodoPanelPresentation Create(
        IReadOnlyList<TodoItemModel>? todos,
        double viewportHeight,
        bool expanded = false)
    {
        var items = todos ?? Array.Empty<TodoItemModel>();
        if (items.Count == 0)
        {
            return new(false, false, 0, 0, 0, string.Empty, string.Empty, string.Empty,
                false, 64, true, Array.Empty<TodoSegmentState>(), 0);
        }

        int done = 0;
        int activeIndex = -1;
        for (int index = 0; index < items.Count; index++)
        {
            if (items[index].IsCompleted)
            {
                done++;
            }

            if (activeIndex < 0 && items[index].IsInProgress)
            {
                activeIndex = index;
            }
        }

        bool allDone = done == items.Count;
        int remaining = items.Count - done;
        int currentIndex = activeIndex >= 0 ? activeIndex : FirstIncomplete(items);
        string current = allDone
            ? $"All {items.Count} tasks complete"
            : currentIndex >= 0 ? items[currentIndex].Content : string.Empty;

        // The next task is the first incomplete item after the current one; a plan
        // that lists a pending item before the active one still gets a next line.
        string? next = null;
        if (!allDone)
        {
            int start = currentIndex + 1;
            for (int index = start; index < items.Count && next is null; index++)
            {
                if (!items[index].IsCompleted)
                {
                    next = items[index].Content;
                }
            }

            for (int index = 0; index < start && next is null; index++)
            {
                if (index != currentIndex && !items[index].IsCompleted)
                {
                    next = items[index].Content;
                }
            }
        }

        string secondary = allDone
            ? "Plan finished"
            : next is not null ? $"Next: {next}" : "Last task in the plan";
        string primary = allDone
            ? $"All {items.Count} tasks complete"
            : expanded
                ? $"{remaining} task{(remaining == 1 ? string.Empty : "s")} remaining"
                : current;

        bool useSegments = items.Count <= MaxSegments;
        TodoSegmentState[] segments = useSegments
            ? items.Select(item => item.IsCompleted ? TodoSegmentState.Done
                : item.IsInProgress ? TodoSegmentState.Active
                : TodoSegmentState.Pending).ToArray()
            : Array.Empty<TodoSegmentState>();

        return new(
            IsVisible: true,
            AllDone: allDone,
            Done: done,
            Total: items.Count,
            Remaining: remaining,
            Current: current,
            HeaderPrimary: primary,
            HeaderSecondary: secondary,
            HasActive: activeIndex >= 0,
            ListHeight: Math.Clamp(viewportHeight * 0.25, 64, MaxListHeight),
            UseSegments: useSegments,
            Segments: segments,
            BarFraction: done / (double)items.Count);
    }

    private static int FirstIncomplete(IReadOnlyList<TodoItemModel> items)
    {
        for (int index = 0; index < items.Count; index++)
        {
            if (!items[index].IsCompleted)
            {
                return index;
            }
        }

        return -1;
    }
}
