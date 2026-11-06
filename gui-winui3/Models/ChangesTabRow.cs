using System.ComponentModel;
using RuApp.Services;

namespace RuApp.Models;

/// <summary>One completed edit of a file in the Changes tab, newest first within its row.</summary>
public sealed record ChangesTabEdit(
    string ActivityId,
    string BeforeText,
    string AfterText,
    int? StartLine,
    int? Added,
    int? Removed,
    string? FileName = null)
{
    public bool HasPreview => BeforeText.Length > 0 || AfterText.Length > 0;
    public bool HasNoPreview => !HasPreview;
    public string LocationText => StartLine is > 0 ? $"Line {StartLine}" : "Edit";
    public string AddedText => TurnChangesProjection.FormatCount(Added, "+");
    public string RemovedText => TurnChangesProjection.FormatCount(Removed, "−");
}

/// <summary>
/// One row of the side panel's Changes tab: every completed edit of one file name
/// in the session, with its own expand state so opening a diff in the panel never
/// folds the transcript's copy of the same edit.
/// </summary>
/// <remarks>
/// Rows group by the edit's workspace-relative path. An edit made through an absolute
/// path carries only its basename; it joins the one relative path with that name when
/// there is exactly one, and otherwise groups by name alone.
/// </remarks>
public sealed class ChangesTabRow : INotifyPropertyChanged
{
    public ChangesTabRow(string fileName, int? added, int? removed, IReadOnlyList<ChangesTabEdit> edits,
        string? firstActivityId = null, string? relativePath = null)
    {
        FileName = fileName;
        RelativePath = relativePath;
        int slash = relativePath?.LastIndexOf('/') ?? -1;
        Folder = slash > 0 ? relativePath![..slash] : null;
        IsScratch = ChangesTabFiles.IsScratch(fileName);
        Added = added;
        Removed = removed;
        Edits = edits;
        FirstActivityId = firstActivityId ?? (edits.Count > 0 ? edits[^1].ActivityId : string.Empty);
    }

    public string FileName { get; }

    /// <summary>The workspace-relative path, when the edits carried one.</summary>
    public string? RelativePath { get; }

    public bool HasRelativePath => RelativePath is not null;

    /// <summary>The folder part of <see cref="RelativePath"/>; null at the workspace root or when unknown.</summary>
    public string? Folder { get; }

    public bool HasFolder => Folder is not null;

    /// <summary>An agent's throwaway file (probe script, patch draft, backup).</summary>
    public bool IsScratch { get; }

    /// <summary>Scratch files stay readable but step back from the real changes.</summary>
    public double RowOpacity => IsScratch ? 0.6 : 1.0;

    public int? Added { get; }

    public int? Removed { get; }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ChangesTabEdit> Edits { get; }

    /// <summary>The file's first edit in the session: with the name, it identifies the row across refreshes.</summary>
    public string FirstActivityId { get; }

    public string Key => (RelativePath ?? FileName).ToUpperInvariant() + "|" + FirstActivityId;

    public bool Contains(string? activityId) =>
        activityId is not null && Edits.Any(edit => edit.ActivityId == activityId);

    public bool HasSameContent(ChangesTabRow other) =>
        Key == other.Key && FileName == other.FileName && RelativePath == other.RelativePath
        && Added == other.Added && Removed == other.Removed
        && Edits.SequenceEqual(other.Edits);

    public string AddedDeltaText => TurnChangesProjection.FormatCount(Added, "+");

    public string RemovedDeltaText => TurnChangesProjection.FormatCount(Removed, "−");

    public string EditCountText => Edits.Count > 1 ? $"{Edits.Count} edits" : string.Empty;

    public string ToolTipText => Edits.Count == 1
        ? $"{RelativePath ?? FileName}, 1 edit"
        : $"{RelativePath ?? FileName}, {Edits.Count} edits";

    public string AccessibleName => $"{ToolTipText}, added {AddedDeltaText}, removed {RemovedDeltaText}. Toggle changes";

    private bool _isExpanded;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpansionGlyph)));
        }
    }

    public string ExpansionGlyph => _isExpanded ? "" : "";

    public event PropertyChangedEventHandler? PropertyChanged;
}
