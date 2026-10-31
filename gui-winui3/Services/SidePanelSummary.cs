using RuApp.Models;

namespace RuApp.Services;

/// <summary>The card shows a bounded preview; the panel retains the complete observation.</summary>
internal static class SidePanelSummary
{
    internal const int TaskPreviewLimit = 3;

    internal static IReadOnlyList<AgentTaskRow> PreviewTasks(IReadOnlyList<AgentTaskRow> rows) =>
        rows.OrderByDescending(row => row.State == "running").Take(TaskPreviewLimit).ToArray();

    internal static string ChangesLabel(IReadOnlyList<ChangesTabRow> rows)
    {
        // Activities carry safe basenames, not canonical paths; two Program.cs
        // entries may be different files. Report recorded edits without guessing.
        int count = rows.Count;
        return count == 0 ? "No file changes" : count == 1 ? "1 file edit"
            : count >= ChangesTabProjection.MaxRows ? $"{count} recent file edits" : $"{count} file edits";
    }
}
