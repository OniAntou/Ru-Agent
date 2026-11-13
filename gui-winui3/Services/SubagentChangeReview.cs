using RuAgent.Protocol.Subagents;

namespace RuApp.Services;

/// <summary>
/// One file of a read-only change review, ready to draw: either a real diff with a
/// copyable patch, or a note saying why none can be shown.
/// </summary>
/// <param name="Fallback">Proposed content shown as plain text when the change is too large to diff.</param>
internal sealed record SubagentReviewFile(string Path, string StatusLabel, string? Note,
    SubagentFileDiff? Diff, string? Fallback, string? Patch)
{
    public int Added => Diff?.Added ?? 0;
    public int Removed => Diff?.Removed ?? 0;
}

/// <summary>
/// Decides what a review may show for each proposed file. A patch is offered only when
/// both sides are complete text and the workspace still matches the captured base:
/// a binary, truncated or diverged file must never become a plausible-looking patch.
/// </summary>
internal static class SubagentChangeReview
{
    internal static SubagentReviewFile File(SubagentChangedFileResponse file)
    {
        var label = StatusLabel(file.Status);
        SubagentReviewFile Note(string note) => new(file.Path, label, note, null, null, null);

        if (file.Binary) return Note("Binary file. Open it in the workspace to inspect the proposed bytes.");
        if (file.Truncated) return Note("Too large for the bounded preview. Ask the main agent, or inspect the file after it is applied.");
        (string? Before, string? After)? sides = file.Status switch
        {
            "base" when file.BaseText is not null && file.ProposedText is not null => (file.BaseText, file.ProposedText),
            "created" when file.ProposedText is not null => (null, file.ProposedText),
            "deleted" when file.BaseText is not null => (file.BaseText, null),
            _ => null,
        };
        if (sides is not { } pair)
        {
            return Note(file.Status switch
            {
                "applied" => "Already integrated into the workspace.",
                "diverged" => "The workspace file changed since the child captured its base; no clean diff is available.",
                "missing" => "The workspace file this change was based on no longer exists.",
                _ => "No preview is available for this file.",
            });
        }

        if (SubagentDiff.Compute(pair.Before, pair.After) is not { } diff)
        {
            return new(file.Path, label, "Too many changed lines to diff here; showing the proposed content instead.",
                null, pair.After, null);
        }

        if (diff.Hunks.Count == 0)
        {
            return Note(file.Status == "base" ? "No text changes." : "Empty file.");
        }

        return new(file.Path, label, null, diff, null, SubagentPatchFormatter.Format(file.Path, pair.Before, pair.After, diff));
    }

    internal static string StatusLabel(string status) => status switch
    {
        "base" => "modified",
        "created" => "new file",
        "applied" => "already integrated",
        "deleted" => "deleted",
        "missing" => "file missing",
        "diverged" => "workspace changed",
        _ => status,
    };

    /// <summary>"3 files · +42 −7"; counts cover only the files that have a real diff.</summary>
    internal static string Summary(IReadOnlyList<SubagentReviewFile> files)
    {
        var count = files.Count == 1 ? "1 file" : $"{files.Count} files";
        var added = files.Sum(file => file.Added);
        var removed = files.Sum(file => file.Removed);
        var withoutDiff = files.Count(file => file.Diff is null);
        return count + (added + removed > 0 ? $" · +{added} −{removed}" : "")
            + (withoutDiff > 0 && withoutDiff < files.Count ? $" · {withoutDiff} without a diff" : "");
    }
}
