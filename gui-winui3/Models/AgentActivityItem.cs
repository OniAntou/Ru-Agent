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
        AgentActivityKind.Thinking => "Đang suy nghĩ",
        AgentActivityKind.Explore => Count is { } count ? $"Đang khám phá {count} mục" : "Đang khám phá",
        AgentActivityKind.Search => Symbol is { Length: > 0 } symbol ? $"Đang tìm {symbol}" : "Đang tìm kiếm",
        AgentActivityKind.Analyze => "Đang phân tích",
        AgentActivityKind.Edit => "Đang sửa",
        AgentActivityKind.Terminal => "Đang thực hiện tác vụ terminal",
        AgentActivityKind.Status when SafeCode == "frames_dropped" => "Một số bước tạm thời đã bị bỏ qua",
        _ => "Đang làm…",
    };

    public string CompletedText => Kind switch
    {
        AgentActivityKind.Thinking => DurationText(),
        AgentActivityKind.Explore => Count is { } count ? $"Đã khám phá {count} mục" : "Đã khám phá",
        AgentActivityKind.Search => FormatSearch("Đã tìm"),
        AgentActivityKind.Analyze => "Đã phân tích",
        AgentActivityKind.Edit => "Đã sửa",
        AgentActivityKind.Terminal => "Đã hoàn thành tác vụ terminal",
        AgentActivityKind.Status when SafeCode == "frames_dropped" => "Một số bước tạm thời đã bị bỏ qua",
        _ => "Đã hoàn tất",
    };

    public string WaitingText => Kind == AgentActivityKind.Status
        ? "Đang chờ anh xác nhận"
        : $"Đang chờ tiếp tục · {SafeCode}";

    public string FailedText => SafeCode is { Length: > 0 }
        ? $"Không thể hoàn tất · {SafeCode}"
        : "Không thể hoàn tất";

    public string MetadataText
    {
        get
        {
            var parts = new List<string>();
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
                parts.Add($"{count} kết quả");
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
        _ => "Đang làm…",
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
            return $"{prefix} {symbol} · {count} kết quả";
        }

        return Symbol is { Length: > 0 } ? $"{prefix} {Symbol}" : $"{prefix} kiếm";
    }

    private string DurationText()
    {
        if (DurationMs is not { } duration)
        {
            return "Đã suy nghĩ";
        }

        return duration < 1000
            ? "Đã suy nghĩ dưới 1 giây"
            : $"Đã suy nghĩ {Math.Round(duration / 1000d):0} giây";
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
