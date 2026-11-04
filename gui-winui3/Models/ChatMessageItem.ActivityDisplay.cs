using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;

namespace RuApp.Models;

public partial class ChatMessageItem
{
    private bool _compactAgentActivity;
    private bool _autoCollapseCompleted;
    private bool _isActivityDetailsExpanded;
    private ObservableCollection<AssistantSegment> _compactTextSegments = new();

    /// <summary>Local presentation preference; never changes stored activity or text.</summary>
    public bool CompactAgentActivity
    {
        get => _compactAgentActivity;
        set
        {
            if (SetProperty(ref _compactAgentActivity, value)) NotifyActivityDisplayChanged();
        }
    }

    /// <summary>Per-message disclosure state, retained while new activities arrive.</summary>
    public bool IsActivityDetailsExpanded
    {
        get => _isActivityDetailsExpanded;
        set
        {
            if (SetProperty(ref _isActivityDetailsExpanded, value)) NotifyActivityDisplayChanged();
        }
    }

    public bool ActivityDetailsCollapsed => ShowActivitySummary && !IsActivityDetailsExpanded;

    /// <summary>
    /// Whether this turn has a compact activity summary. Compact mode shows one from
    /// the start; verbose mode gets one only after a finished turn is auto-collapsed,
    /// so a running turn keeps every row.
    /// </summary>
    public bool ShowActivitySummary => CompactAgentActivity || AutoCollapseCompleted;

    /// <summary>True once a finished verbose turn collapsed its own process rows.</summary>
    public bool AutoCollapseCompleted => _autoCollapseCompleted;

    /// <summary>
    /// Collapses a finished assistant turn that has an answer and no row still
    /// running or waiting. Failed turns without an answer stay verbose so the
    /// failure is visible without an extra click.
    /// </summary>
    internal void AutoCollapseCompletedTurn()
    {
        if (_autoCollapseCompleted
            || IsStreaming
            || Role != "assistant"
            || !HasText
            || AgentActivities is not { Count: > 0 } activities
            || activities.Any(item => item.IsRunning || item.IsWaiting))
        {
            return;
        }

        _autoCollapseCompleted = true;
        OnPropertyChanged(nameof(AutoCollapseCompleted));
        NotifyActivityDisplayChanged();
    }

    public ObservableCollection<AssistantSegment>? DisplayedSegments =>
        ActivityDetailsCollapsed && Segments is not null ? _compactTextSegments : Segments;

    private void NotifyActivityDisplayChanged()
    {
        OnPropertyChanged(nameof(ActivityDetailsCollapsed));
        OnPropertyChanged(nameof(ShowActivitySummary));
        OnPropertyChanged(nameof(DisplayedSegments));
        OnPropertyChanged(nameof(ShowLegacyActivityFeed));
        UpdateActivityGaps();
    }

    /// <summary>Space between a reply's prose and the activity rows beside it.</summary>
    internal const double ActivityGapSize = 12;

    /// <summary>
    /// Gives each prose slice room on the side where an activity row sits, so the rows
    /// read as their own block instead of running into the reply. Computed over what
    /// is displayed: a collapsed compact turn shows prose only and needs no gaps.
    /// </summary>
    private void UpdateActivityGaps()
    {
        if (DisplayedSegments is not { } shown) return;
        for (var i = 0; i < shown.Count; i++)
        {
            if (shown[i] is not TextSegment text) continue;
            var before = i > 0 && shown[i - 1] is ActivitySegment ? ActivityGapSize : 0;
            var after = i + 1 < shown.Count && shown[i + 1] is ActivitySegment ? ActivityGapSize : 0;
            text.ActivityGap = new Microsoft.UI.Xaml.Thickness(0, before, 0, after);
        }
    }

    /// <summary>True while the collapsed view shows the settled fallback rather than the tail.</summary>
    private bool _compactShowsFallback;

    /// <summary>
    /// The prose a collapsed turn shows: only the text after its last activity row.
    /// Narration written between rows belongs to the process the summary line folds,
    /// so it reappears in place when the turn is expanded instead of piling up under
    /// the summary. A settled turn whose tail is empty (it ended on an activity) shows
    /// its last prose instead, so a collapsed turn never hides its final answer.
    /// </summary>
    private void UpdateCompactSegments(NotifyCollectionChangedEventArgs? change = null)
    {
        // A live text-only append extends the shown tail. Keep those text objects,
        // including their streaming/error state, without rescanning history. An
        // appended activity starts a new tail and rebuilds below.
        if (!_compactShowsFallback
            && change?.Action == NotifyCollectionChangedAction.Add && change.NewItems is { } added
            && change.NewStartingIndex == Segments!.Count - added.Count
            && !added.OfType<ActivitySegment>().Any())
        {
            foreach (var item in added)
                if (item is TextSegment text) _compactTextSegments.Add(text);
            UpdateActivityGaps();
            return;
        }

        _compactTextSegments = new ObservableCollection<AssistantSegment>(CompactProse(out _compactShowsFallback));
        OnPropertyChanged(nameof(DisplayedSegments));
        UpdateActivityGaps();
    }

    private IEnumerable<TextSegment> CompactProse(out bool isFallback)
    {
        isFallback = false;
        if (Segments is not { } segments) return Enumerable.Empty<TextSegment>();

        var tailStart = 0;
        for (var i = segments.Count - 1; i >= 0; i--)
        {
            if (segments[i] is ActivitySegment)
            {
                tailStart = i + 1;
                break;
            }
        }

        var tail = segments.Skip(tailStart).OfType<TextSegment>().ToList();
        if (IsStreaming || tail.Any(text => text.HasText)) return tail;

        var lastProse = segments.Take(tailStart).OfType<TextSegment>().LastOrDefault(text => text.HasText);
        if (lastProse is null) return tail;
        isFallback = true;
        return new[] { lastProse };
    }
}
