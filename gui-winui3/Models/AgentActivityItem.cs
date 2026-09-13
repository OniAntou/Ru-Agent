using System.ComponentModel;
using System.Runtime.CompilerServices;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuApp.Models;

public sealed class AgentActivityItem : INotifyPropertyChanged
{
    private AgentActivityKind _kind;
    private AgentActivityState _state;
    private string? _fileName;
    private int? _lineStart;
    private int? _lineEnd;
    private string? _symbol;
    private int? _count;
    private int? _added;
    private int? _removed;
    private int? _durationMs;
    private string _safeCode = "succeeded";
    private string? _toolDisplay;
    private string? _detail;

    public AgentActivityItem(AgentActivityModel activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ActivityId = activity.ActivityId;
        Apply(activity);
    }

    public string ActivityId { get; }

    public AgentActivityKind Kind
    {
        get => _kind;
        private set => SetProperty(ref _kind, value);
    }

    public AgentActivityState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public string? FileName
    {
        get => _fileName;
        private set => SetProperty(ref _fileName, value);
    }

    public int? LineStart
    {
        get => _lineStart;
        private set => SetProperty(ref _lineStart, value);
    }

    public int? LineEnd
    {
        get => _lineEnd;
        private set => SetProperty(ref _lineEnd, value);
    }

    public string? Symbol
    {
        get => _symbol;
        private set => SetProperty(ref _symbol, value);
    }

    public int? Count
    {
        get => _count;
        private set => SetProperty(ref _count, value);
    }

    public int? Added
    {
        get => _added;
        private set => SetProperty(ref _added, value);
    }

    public int? Removed
    {
        get => _removed;
        private set => SetProperty(ref _removed, value);
    }

    public int? DurationMs
    {
        get => _durationMs;
        private set => SetProperty(ref _durationMs, value);
    }

    public string SafeCode
    {
        get => _safeCode;
        private set => SetProperty(ref _safeCode, value);
    }

    public string? ToolDisplay
    {
        get => _toolDisplay;
        private set => SetProperty(ref _toolDisplay, value);
    }

    public string? Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    private bool _isVisibleInFeed = true;

    /// <summary>Feed grouping collapses redundant rows; collapsed rows keep
    /// their data for history but leave the visible feed.</summary>
    public bool IsVisibleInFeed
    {
        get => _isVisibleInFeed;
        set => SetProperty(ref _isVisibleInFeed, value);
    }

    private int _collapsedDurationMs;

    /// <summary>Milliseconds folded in from collapsed same-group rows.</summary>
    public int CollapsedDurationMs
    {
        get => _collapsedDurationMs;
        set
        {
            if (SetProperty(ref _collapsedDurationMs, value))
            {
                OnPropertyChanged(nameof(EffectiveDurationMs));
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(CompletedText));
                OnPropertyChanged(nameof(MetadataText));
            }
        }
    }

    public int? EffectiveDurationMs
    {
        get
        {
            if (DurationMs is { } own)
            {
                return own + CollapsedDurationMs;
            }

            return CollapsedDurationMs > 0 ? CollapsedDurationMs : null;
        }
    }

    public bool IsRunning => State == AgentActivityState.Started;
    public bool IsCompleted => State == AgentActivityState.Completed;
    public bool IsFailed => State == AgentActivityState.Failed;
    public bool IsWaiting => State == AgentActivityState.Waiting;

    public string StatusGlyph => State switch
    {
        AgentActivityState.Started => "\uE895",
        AgentActivityState.Completed => "\uE73E",
        AgentActivityState.Failed => "\uEA39",
        AgentActivityState.Waiting => "\uE823",
        _ => "\uE895",
    };

    public string StartedText => Kind switch
    {
        AgentActivityKind.Thinking => "Thinking",
        AgentActivityKind.Explore => FileName is { Length: > 0 } file
            ? $"Exploring {file}"
            : Count is { } count ? (count == 1 ? "Exploring 1 item" : $"Exploring {count} items") : "Exploring",
        AgentActivityKind.Search => Symbol is { Length: > 0 } symbol ? $"Searching for {symbol}" : "Searching",
        AgentActivityKind.Analyze => "Analyzing",
        AgentActivityKind.Edit => ToolDisplay switch
        {
            "Delete" => "Deleting",
            "Restore" => "Restoring",
            _ => "Editing",
        },
        AgentActivityKind.Terminal => !string.IsNullOrWhiteSpace(ToolDisplay) ? $"Running {ToolDisplay} task" : "Running terminal task",
        AgentActivityKind.Compaction => Count is { } compacting
            ? (compacting == 1 ? "Compacting 1 context item" : $"Compacting {compacting} context items")
            : "Compacting context",
        AgentActivityKind.Retry => Count is { } attempt ? $"Retrying (attempt {attempt})" : "Retrying",
        AgentActivityKind.Status when SafeCode == "frames_dropped" => "Some steps were temporarily skipped",
        _ => "Working…",
    };

    public string CompletedText => Kind switch
    {
        AgentActivityKind.Thinking => DurationText(),
        AgentActivityKind.Explore => FileName is { Length: > 0 } file
            ? $"Explored {file}"
            : Count is { } count ? (count == 1 ? "Explored 1 item" : $"Explored {count} items") : "Explored",
        AgentActivityKind.Search => FormatSearch("Searched"),
        AgentActivityKind.Analyze => "Analyzed",
        AgentActivityKind.Edit => ToolDisplay switch
        {
            "Delete" => "Deleted",
            "Restore" => "Restored",
            _ => "Edited",
        },
        AgentActivityKind.Terminal => !string.IsNullOrWhiteSpace(ToolDisplay) ? $"Completed {ToolDisplay} task" : "Completed terminal task",
        AgentActivityKind.Compaction => Count is { } compacted
            ? (compacted == 1 ? "Compacted 1 context item" : $"Compacted {compacted} context items")
            : "Compacted context",
        AgentActivityKind.Retry => "Retried",
        AgentActivityKind.Status when SafeCode == "frames_dropped" => "Some steps were temporarily skipped",
        _ => "Completed",
    };

    public string WaitingText => Kind == AgentActivityKind.Status
        ? "Waiting for confirmation"
        : $"Waiting to continue · {SafeCode}";

    public string FailedText => SafeCode is { Length: > 0 }
        ? $"Failed · {SafeCode}"
        : "Failed";

    public bool HasLineDelta => Kind == AgentActivityKind.Edit && (Added is not null || Removed is not null);
    public bool HasNoLineDelta => !HasLineDelta;
    public bool HasLineDeltaAndBaseMetadata => HasLineDelta && !string.IsNullOrWhiteSpace(BaseMetadataText);
    public string AddedDeltaText => $"+{Added ?? 0}";
    public string RemovedDeltaText => $"−{Removed ?? 0}";

    public string BaseMetadataText
    {
        get
        {
            var parts = new List<string>();
            // ToolDisplay is intentionally omitted: every DisplayText already
            // names the verb (Exploring/Editing/Running …), so repeating it
            // here produced "Explored Explore" style duplication.
            // Explore target is likewise in DisplayText ("Explored {file}").
            if (!string.IsNullOrWhiteSpace(FileName) && Kind != AgentActivityKind.Explore)
            {
                parts.Add(FileName!);
            }

            if (LineStart is { } start && LineEnd is { } end)
            {
                parts.Add($"L{start}–{end}");
            }

            if (!string.IsNullOrWhiteSpace(Symbol) && Kind != AgentActivityKind.Search)
            {
                parts.Add(Symbol!);
            }

            if (Count is { } count && Kind == AgentActivityKind.Search)
            {
                parts.Add(count == 1 ? "1 result" : $"{count} results");
            }

            return string.Join(" · ", parts);
        }
    }

    public string MetadataText
    {
        get
        {
            var parts = new List<string>();
            var baseMeta = BaseMetadataText;
            if (!string.IsNullOrWhiteSpace(baseMeta))
            {
                parts.Add(baseMeta);
            }

            if (HasLineDelta)
            {
                parts.Add($"{AddedDeltaText} {RemovedDeltaText}");
            }

            return string.Join(" · ", parts);
        }
    }

    public string DisplayText => State switch
    {
        AgentActivityState.Started => StartedText,
        AgentActivityState.Completed => CompletedText,
        AgentActivityState.Waiting => WaitingText,
        AgentActivityState.Failed => FailedText,
        _ => "Working…",
    };

    public void UpdateFrom(AgentActivityModel activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (!string.Equals(ActivityId, activity.ActivityId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Activity id cannot change.", nameof(activity));
        }

        Apply(activity);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Apply(AgentActivityModel activity)
    {
        Kind = activity.Kind;
        State = activity.State;
        FileName = activity.FileName;
        LineStart = activity.LineStart;
        LineEnd = activity.LineEnd;
        Symbol = activity.Symbol;
        Count = activity.Count;
        Added = activity.Added;
        Removed = activity.Removed;
        DurationMs = activity.DurationMs;
        SafeCode = activity.SafeCode;
        ToolDisplay = activity.ToolDisplay;
        Detail = activity.Detail;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(StartedText));
        OnPropertyChanged(nameof(CompletedText));
        OnPropertyChanged(nameof(WaitingText));
        OnPropertyChanged(nameof(FailedText));
        OnPropertyChanged(nameof(MetadataText));
        OnPropertyChanged(nameof(BaseMetadataText));
        OnPropertyChanged(nameof(HasLineDelta));
        OnPropertyChanged(nameof(HasNoLineDelta));
        OnPropertyChanged(nameof(HasLineDeltaAndBaseMetadata));
        OnPropertyChanged(nameof(AddedDeltaText));
        OnPropertyChanged(nameof(RemovedDeltaText));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(EffectiveDurationMs));
        OnPropertyChanged(nameof(Detail));
    }

    private string FormatSearch(string prefix)
    {
        if (Symbol is { Length: > 0 } symbol && Count is { } count)
        {
            var resultSuffix = count == 1 ? "1 result" : $"{count} results";
            return $"{prefix} for {symbol} · {resultSuffix}";
        }

        return Symbol is { Length: > 0 } ? $"{prefix} for {Symbol}" : prefix;
    }

    private string DurationText()
    {
        if (EffectiveDurationMs is not { } duration)
        {
            return "Thought";
        }

        if (duration < 1000)
        {
            return "Thought for under 1 second";
        }

        var seconds = Math.Round(duration / 1000d);
        return seconds == 1
            ? "Thought for 1 second"
            : $"Thought for {seconds:0} seconds";
    }

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
