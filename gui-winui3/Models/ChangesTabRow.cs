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
    int? Removed)
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
/// Activities carry safe basenames, not paths, so two files with the same name in
/// different folders share a row; the edit count and per-edit lines keep that visible.
/// </remarks>
public sealed class ChangesTabRow : INotifyPropertyChanged
{
    public ChangesTabRow(string fileName, int? added, int? removed, IReadOnlyList<ChangesTabEdit> edits,
        string? firstActivityId = null)
    {
        FileName = fileName;
        Added = added;
        Removed = removed;
        Edits = edits;
        FirstActivityId = firstActivityId ?? (edits.Count > 0 ? edits[^1].ActivityId : string.Empty);
    }

    public string FileName { get; }

    public int? Added { get; }

    public int? Removed { get; }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ChangesTabEdit> Edits { get; }

    /// <summary>The file's first edit in the session: with the name, it identifies the row across refreshes.</summary>
    public string FirstActivityId { get; }

    public string Key => FileName.ToUpperInvariant() + "|" + FirstActivityId;

    public bool Contains(string? activityId) =>
        activityId is not null && Edits.Any(edit => edit.ActivityId == activityId);

    public bool HasSameContent(ChangesTabRow other) =>
        Key == other.Key && FileName == other.FileName && Added == other.Added && Removed == other.Removed
        && Edits.SequenceEqual(other.Edits);

    public string AddedDeltaText => TurnChangesProjection.FormatCount(Added, "+");

    public string RemovedDeltaText => TurnChangesProjection.FormatCount(Removed, "−");

    public string EditCountText => Edits.Count > 1 ? $"{Edits.Count} edits" : string.Empty;

    public string ToolTipText => Edits.Count == 1 ? $"{FileName}, 1 edit" : $"{FileName}, {Edits.Count} edits";

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
