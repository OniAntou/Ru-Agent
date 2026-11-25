using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using RuAgent.Protocol.Harness;

namespace RuApp.Models;

public partial class ChatMessageItem
{
    private bool _compactAgentActivity;
    private bool _autoCollapseCompleted;
    private bool _isActivityDetailsExpanded;
    private double _contentWidth = 720;
    private ObservableCollection<AssistantSegment> _compactTextSegments = new();

    /// <summary>Reading-column width applied to this row; cheap to update on every drag.</summary>
    public double ContentWidth
    {
        get => _contentWidth;
        set => SetProperty(ref _contentWidth, value);
    }

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

    /// <summary>Whether the turn has process rows to fold behind the header.</summary>
    public bool HasProcess =>
        HasAgentActivities || Segments?.Any(segment => segment is ActivitySegment) == true;

    public bool ShowProcessHeader => ShowActivitySummary && HasProcess;

    private IEnumerable<AgentActivityItem> ProcessSteps
    {
        get
        {
            var segmented = Segments?.OfType<ActivitySegment>()
                .Select(segment => segment.Item).ToList();
            if (segmented is { Count: > 0 }) return segmented;
            return AgentActivities ?? Enumerable.Empty<AgentActivityItem>();
        }
    }

    public string ProcessGlyph => KindGlyph(ProcessSteps.LastOrDefault()?.Kind);

    public bool ProcessIsFailed => ProcessSteps.Any(item => item.IsFailed);

    public bool ProcessIsWaiting => ProcessSteps.Any(item => item.IsWaiting);

    /// <summary>The process header's projection over the rows it folds; live while the turn streams.</summary>
    private RuApp.Services.AgentActivitySummary ProcessSummary =>
        RuApp.Services.AgentActivitySummaryProjection.ForSteps(ProcessSteps, IsStreaming);

    /// <summary>
    /// The step count while the turn works; once it ends, the counted recap before it
    /// ("Read 5 files, searched 3 times and edited 2 files · 12 steps").
    /// </summary>
    public string ProcessMeta
    {
        get
        {
            if (!ShowProcessHeader) return string.Empty;
            var summary = ProcessSummary;
            // A live thinking step keeps its reasoning line in the header's tail: the
            // collapsed turn then reads "Thinking · <first line>", like the row does.
            if (IsStreaming && summary.LiveDetail.Length > 0) return summary.LiveDetail;
            if (summary.Count == 0) return IsStreaming || summary.Recap.Length == 0 ? string.Empty : summary.Recap;
            return IsStreaming || summary.Recap.Length == 0 ? summary.StepsText : $"{summary.Recap} · {summary.StepsText}";
        }
    }

    private static string KindGlyph(AgentActivityKind? kind) => kind switch
    {
        AgentActivityKind.Explore => "\uE8A5",
        AgentActivityKind.Search => "\uE721",
        AgentActivityKind.Edit => "\uE70F",
        AgentActivityKind.Terminal => "\uE756",
        AgentActivityKind.Todo => "\uE9D5",
        AgentActivityKind.Compaction => "\uE73F",
        AgentActivityKind.Retry => "\uE72C",
        AgentActivityKind.Thinking => "\uE82F",
        _ => "\uE945",
    };

    public TimeSpan? Duration =>
        _completedAt is { } end && end >= Timestamp ? end - Timestamp : null;

    /// <summary>DSH-style disclosure title: Worked for / Took, with a named failure state.</summary>
    public string ProcessHeader
    {
        get
        {
            if (!ShowProcessHeader) return string.Empty;
            // Live: the step in progress as its row says it, so the line moves with the run.
            if (IsStreaming) return ProcessSummary.Title;
            var duration = Duration;
            if (IsError)
            {
                return duration is { } failed ? $"Took {FormatDuration(failed)} · failed" : "Failed";
            }

            return duration is { } completed ? $"Worked for {FormatDuration(completed)}" : "Worked";
        }
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        return $"{Math.Max(1, (int)Math.Round(duration.TotalSeconds))}s";
    }

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
        OnPropertyChanged(nameof(HasProcess));
        OnPropertyChanged(nameof(ShowProcessHeader));
        OnPropertyChanged(nameof(ProcessHeader));
        OnPropertyChanged(nameof(ProcessGlyph));
        OnPropertyChanged(nameof(ProcessMeta));
        OnPropertyChanged(nameof(ProcessIsFailed));
        OnPropertyChanged(nameof(ProcessIsWaiting));
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
