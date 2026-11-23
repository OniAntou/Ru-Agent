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
        ChangesTabEdit? previewEdit = null, string? firstActivityId = null, string? relativePath = null)
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
        // The row draws one hunk: the file's newest recorded edit. The newest-first Edits list
        // is only the identity/cap window; callers project the hunk from the full list.
        PreviewEdit = previewEdit ?? edits.FirstOrDefault(edit => edit.HasPreview);
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

    /// <summary>Newest first; the row's identity and reveal window, not its display list.</summary>
    public IReadOnlyList<ChangesTabEdit> Edits { get; }

    /// <summary>
    /// The hunk the expansion draws: the file's newest edit that recorded text, if any. The
    /// numbers beside the row are that edit's own counts, so the block and the number agree.
    /// </summary>
    public ChangesTabEdit? PreviewEdit { get; }

    /// <summary>The recorded "before" of the drawn hunk; empty when there is none.</summary>
    public string PreviewBeforeText => PreviewEdit?.BeforeText ?? string.Empty;

    /// <summary>The recorded "after" of the drawn hunk; empty when there is none.</summary>
    public string PreviewAfterText => PreviewEdit?.AfterText ?? string.Empty;

    /// <summary>The line the drawn hunk reported, when it reported one.</summary>
    public int? PreviewStartLine => PreviewEdit?.StartLine;

    /// <summary>
    /// Whether the row has a diff to show. An image or other edit with no text before/after
    /// has nothing to draw, so it is not expandable.
    /// </summary>
    public bool HasPreview => PreviewEdit is not null;

    /// <summary>The file's first edit in the session: with the name, it identifies the row across refreshes.</summary>
    public string FirstActivityId { get; }

    public string Key => (RelativePath ?? FileName).ToUpperInvariant() + "|" + FirstActivityId;

    public bool Contains(string? activityId) =>
        activityId is not null && Edits.Any(edit => edit.ActivityId == activityId);

    public bool HasSameContent(ChangesTabRow other) =>
        Key == other.Key && FileName == other.FileName && RelativePath == other.RelativePath
        && Added == other.Added && Removed == other.Removed
        && Edits.SequenceEqual(other.Edits) && Equals(PreviewEdit, other.PreviewEdit);

    public string AddedDeltaText => TurnChangesProjection.FormatCount(Added, "+");

    public string RemovedDeltaText => TurnChangesProjection.FormatCount(Removed, "−");

    /// <summary>
    /// The removed column of a row: empty when nothing was removed or the count is unknown,
    /// so a list of new files is not a column of dashes. The accessible name and the
    /// expanded edits still state the recorded value.
    /// </summary>
    public string RemovedColumnText => Removed is > 0 ? RemovedDeltaText : string.Empty;

    public bool HasRemovedColumn => Removed is > 0;

    /// <summary>The folder as it follows the file name on the row's single line.</summary>
    public string FolderSuffix => Folder is null ? string.Empty : "  " + Folder;

    public string EditCountText => Edits.Count > 1 ? $"{Edits.Count} edits" : string.Empty;

    public string ToolTipText => Edits.Count == 1
        ? $"{RelativePath ?? FileName}, 1 edit"
        : $"{RelativePath ?? FileName}, {Edits.Count} edits";

    public string AccessibleName => $"{ToolTipText}, added {AddedDeltaText}, removed {RemovedDeltaText}. Toggle changes";

    private string? _currentText;

    /// <summary>
    /// The file as it stands on disk, supplied by the view when the row opens so the diff
    /// can show the whole file with the edit in place. Null until it is read, and left null
    /// when the file is gone or unreadable — the recorded fragment is still drawn then.
    /// </summary>
    public string? CurrentText
    {
        get => _currentText;
        set
        {
            if (string.Equals(_currentText, value, StringComparison.Ordinal)) return;
            _currentText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentText)));
        }
    }

    private bool _isExpanded;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            // A row with no recorded version comparison has nothing to expand.
            if (_isExpanded == value || (value && !HasPreview))
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
