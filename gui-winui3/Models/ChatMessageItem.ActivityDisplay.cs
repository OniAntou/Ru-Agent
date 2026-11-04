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

    private void UpdateCompactSegments(NotifyCollectionChangedEventArgs? change = null)
    {
        // Live activity adds append an activity and a text tail. Keep those text
        // objects, including their streaming/error state, without rescanning history.
        if (change?.Action == NotifyCollectionChangedAction.Add && change.NewItems is { } added
            && change.NewStartingIndex == Segments!.Count - added.Count)
        {
            foreach (var item in added)
                if (item is TextSegment text) _compactTextSegments.Add(text);
            UpdateActivityGaps();
            return;
        }

        _compactTextSegments = new ObservableCollection<AssistantSegment>(
            Segments?.OfType<TextSegment>() ?? Enumerable.Empty<TextSegment>());
        OnPropertyChanged(nameof(DisplayedSegments));
        UpdateActivityGaps();
    }
}
