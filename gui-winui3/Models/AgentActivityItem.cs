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
        AgentActivityKind.Explore => Count is { } count ? (count == 1 ? "Exploring 1 item" : $"Exploring {count} items") : "Exploring",
        AgentActivityKind.Search => Symbol is { Length: > 0 } symbol ? $"Searching for {symbol}" : "Searching",
        AgentActivityKind.Analyze => "Analyzing",
        AgentActivityKind.Edit => ToolDisplay switch
        {
            "Delete" => "Deleting",
            "Restore" => "Restoring",
            _ => "Editing",
        },
        AgentActivityKind.Terminal => !string.IsNullOrWhiteSpace(ToolDisplay) ? $"Running {ToolDisplay} task" : "Running terminal task",
        AgentActivityKind.Status when SafeCode == "frames_dropped" => "Some steps were temporarily skipped",
        _ => "Working…",
    };

    public string CompletedText => Kind switch
    {
        AgentActivityKind.Thinking => DurationText(),
        AgentActivityKind.Explore => Count is { } count ? (count == 1 ? "Explored 1 item" : $"Explored {count} items") : "Explored",
        AgentActivityKind.Search => FormatSearch("Searched"),
        AgentActivityKind.Analyze => "Analyzed",
        AgentActivityKind.Edit => ToolDisplay switch
        {
            "Delete" => "Deleted",
            "Restore" => "Restored",
            _ => "Edited",
        },
        AgentActivityKind.Terminal => !string.IsNullOrWhiteSpace(ToolDisplay) ? $"Completed {ToolDisplay} task" : "Completed terminal task",
        AgentActivityKind.Status when SafeCode == "frames_dropped" => "Some steps were temporarily skipped",
        _ => "Completed",
    };

    public string WaitingText => Kind == AgentActivityKind.Status
        ? "Waiting for confirmation"
        : $"Waiting to continue · {SafeCode}";

    public string FailedText => SafeCode is { Length: > 0 }
        ? $"Failed · {SafeCode}"
        : "Failed";

    public string MetadataText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ToolDisplay))
            {
                parts.Add(ToolDisplay!);
            }

            if (!string.IsNullOrWhiteSpace(FileName))
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

            if (Kind == AgentActivityKind.Edit && (Added is not null || Removed is not null))
            {
                parts.Add($"+{Added ?? 0} −{Removed ?? 0}");
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
        OnPropertyChanged(nameof(DisplayText));
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
        if (DurationMs is not { } duration)
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
