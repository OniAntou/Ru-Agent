using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
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

    /// <summary>
    /// Where this edit landed and what it reported, e.g. "line 42 · +3 −1". Each hunk names
    /// its own place, because a row now shows one hunk per edit instead of one comparison.
    /// </summary>
    public string Caption
    {
        get
        {
            var parts = new List<string>();
            if (StartLine is { } line)
            {
                parts.Add("line " + line.ToString(CultureInfo.InvariantCulture));
            }

            var counts = new List<string>();
            if (Added is int added && added > 0)
            {
                counts.Add("+" + added.ToString("N0", CultureInfo.InvariantCulture));
            }

            if (Removed is int removed && removed > 0)
            {
                counts.Add("−" + removed.ToString("N0", CultureInfo.InvariantCulture));
            }

            if (counts.Count > 0)
            {
                parts.Add(string.Join(" ", counts));
            }

            return string.Join(" · ", parts);
        }
    }

    public Visibility CaptionVisibility => Caption.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        IReadOnlyList<ChangesTabEdit>? previewEdits = null, string? firstActivityId = null, string? relativePath = null)
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
        // The row draws one hunk per recorded edit, newest first, so the numbers beside the
        // file add up to what the expansion contains. The newest-first Edits list is only
        // the identity/cap window; callers project the hunks from the full list.
        PreviewEdits = previewEdits ?? edits.Where(edit => edit.HasPreview).ToArray();
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
    /// The hunks the expansion draws: one per edit that recorded text, newest first. Where
    /// it landed and what it cost travel on each <see cref="ChangesTabEdit"/>, so a hunk is
    /// self-describing instead of borrowing a single combined comparison.
    /// </summary>
    public IReadOnlyList<ChangesTabEdit> PreviewEdits { get; }

    /// <summary>
    /// Whether the row has a diff to show. An image or other edit with no text before/after
    /// has nothing to draw, so it is not expandable.
    /// </summary>
    public bool HasPreview => PreviewEdits.Count > 0;

    /// <summary>The file's first edit in the session: with the name, it identifies the row across refreshes.</summary>
    public string FirstActivityId { get; }

    public string Key => (RelativePath ?? FileName).ToUpperInvariant() + "|" + FirstActivityId;

    public bool Contains(string? activityId) =>
        activityId is not null && Edits.Any(edit => edit.ActivityId == activityId);

    public bool HasSameContent(ChangesTabRow other) =>
        Key == other.Key && FileName == other.FileName && RelativePath == other.RelativePath
        && Added == other.Added && Removed == other.Removed
        && Edits.SequenceEqual(other.Edits) && PreviewEdits.SequenceEqual(other.PreviewEdits);

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
