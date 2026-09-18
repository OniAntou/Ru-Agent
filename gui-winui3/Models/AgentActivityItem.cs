using System.ComponentModel;
using System.Runtime.CompilerServices;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuApp.Models;

public sealed class AgentActivityItem : INotifyPropertyChanged
{
    private AgentActivityKind _kind;
    private IReadOnlyList<TodoItemModel>? _todos;
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

    /// <summary>
    /// The agent's task list, or null for an activity that carries none.
    /// </summary>
    /// <remarks>
    /// Rows rather than the wire record, so the template binds to display-ready state
    /// and does not have to know about status strings.
    /// </remarks>
    public IReadOnlyList<TodoItemModel>? Todos
    {
        get => _todos;
        private set
        {
            if (SetProperty(ref _todos, value))
            {
                OnPropertyChanged(nameof(HasTodos));
            }
        }
    }

    public bool HasTodos => _todos is { Count: > 0 };

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

    /// <summary>
    /// Whether the row shows the plain detail line.
    ///
    /// The producer decides when a detail is worth sending: a terminal command while it
    /// waits for approval, an edit preview while the edit runs. The view renders
    /// whatever arrives. It used to hide the detail of a running edit, which is the one
    /// frame that carries the preview at all — so the diff for the edit in front of the
    /// user was the single thing never shown.
    ///
    /// A preview is not a detail line: it renders as two panes, and both at once would
    /// print the change twice.
    /// </summary>
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail) && !IsEditPreview;

    /// <summary>Two-pane edit preview marker: "@@ old ==&gt; new" with escaped newlines.</summary>
    public bool IsEditPreview =>
        Kind == AgentActivityKind.Edit
        && Detail is { } detail
        && detail.StartsWith("@@ ", StringComparison.Ordinal)
        && detail.Contains(" ==> ", StringComparison.Ordinal);

    public bool HasEditPreview => IsEditPreview;

    public string EditBeforeText => IsEditPreview ? SplitEditPreview().Before : string.Empty;

    public string EditAfterText => IsEditPreview ? SplitEditPreview().After : string.Empty;

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
                OnPropertyChanged(nameof(HasDuration));
                OnPropertyChanged(nameof(DurationShortText));
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

    /// <summary>
    /// The run ended before this row's completion frame was applied: the frame can
    /// lose the race with the run's terminal transition, and frames are ignored
    /// once the controller is idle. Settle the row so it never keeps a running
    /// (or forever-waiting) affordance, and report the interruption instead of a
    /// false completion.
    /// </summary>
    public void SettleInterruptedRun()
    {
        if (State != AgentActivityState.Started && State != AgentActivityState.Waiting)
        {
            return;
        }

        State = AgentActivityState.Failed;
        SafeCode = "interrupted";
    }

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
        AgentActivityKind.Todo => "Updating the plan",
        AgentActivityKind.Explore => FileName is { Length: > 0 } file
            ? $"Exploring {file}"
            : Count is { } count ? (count == 1 ? "Exploring 1 item" : $"Exploring {count} items") : "Exploring",
        AgentActivityKind.Search => Symbol is { Length: > 0 } symbol ? $"Searching for {symbol}" : "Searching",
        AgentActivityKind.Analyze => "Analyzing",
        AgentActivityKind.Edit => ToolDisplay switch
        {
            "Delete" => "Deleting",
            "Restore" => "Restoring",
            "Write" => "Writing",
            _ => "Editing",
        },
        AgentActivityKind.Terminal => !string.IsNullOrWhiteSpace(ToolDisplay) ? $"Running {ToolDisplay} task" : "Running terminal task",
        AgentActivityKind.Compaction => Count is { } compacting
            ? (compacting == 1 ? "Compacting 1 context item" : $"Compacting {compacting} context items")
            : "Compacting context",
        AgentActivityKind.Retry => Count is { } attempt ? $"Retrying (attempt {attempt})" : "Retrying",
        AgentActivityKind.Status when SafeCode == "frames_dropped" => "Some steps were temporarily skipped",
        AgentActivityKind.Status => StatusWaitingText(SafeCode),
        _ => "Working…",
    };

    /// <summary>
    /// What a waiting Status row says, by reason.
    /// </summary>
    /// <remarks>
    /// These rows carry the only explanation of why a run is paused, so they state the
    /// reason rather than repeating that something is waiting.
    /// </remarks>
    private static string StatusWaitingText(string safeCode) => safeCode switch
    {
        "inspection_required" => "Waiting: a read-only check is needed first",
        "reconciliation_required" => "Stopped: the last change is unverified",
        { Length: > 0 } code => "Waiting: " + code.Replace('_', ' '),
        _ => "Waiting",
    };

    public string CompletedText => Kind switch
    {
        AgentActivityKind.Thinking => DurationText(),
        AgentActivityKind.Todo => Count is { } planned
            ? (planned == 1 ? "Planned 1 task" : $"Planned {planned} tasks")
            : "Plan updated",
        AgentActivityKind.Explore => FileName is { Length: > 0 } file
            ? $"Explored {file}"
            : Count is { } count ? (count == 1 ? "Explored 1 item" : $"Explored {count} items") : "Explored",
        AgentActivityKind.Search => FormatSearch("Searched"),
        AgentActivityKind.Analyze => "Analyzed",
        AgentActivityKind.Edit => ToolDisplay switch
        {
            "Delete" => "Deleted",
            "Restore" => "Restored",
            "Write" => "Wrote",
            _ => "Edited",
        },
        AgentActivityKind.Terminal => !string.IsNullOrWhiteSpace(ToolDisplay) ? $"Completed {ToolDisplay} task" : "Completed terminal task",
        AgentActivityKind.Compaction => Count is { } compacted
            ? (compacted == 1 ? "Compacted 1 context item" : $"Compacted {compacted} context items")
            : "Compacted context",
        AgentActivityKind.Retry => "Retried",
        AgentActivityKind.Status => StatusText(SafeCode),
        _ => "Completed",
    };

    public string WaitingText => Kind == AgentActivityKind.Status
        ? "Waiting for confirmation"
        : "Waiting to continue";

    /// <summary>
    /// What a Status row says when its safe code names nothing more specific.
    /// </summary>
    /// <remarks>
    /// The old default was the word "Completed", which described the state and not the
    /// activity: several unrelated rows all read the same, and a reader could not tell what
    /// had completed. A status row exists to explain something, so when it has no
    /// explanation it falls back to naming itself rather than to a synonym for "done".
    /// </remarks>
    private static string StatusText(string safeCode) => safeCode switch
    {
        "frames_dropped" => "Some steps were temporarily skipped",
        "inspection_required" => "Waiting: an action needs a read-only check first",
        "reconciliation_required" => "Stopped: the last change is unverified",
        { Length: > 0 } code => "Status: " + code.Replace('_', ' '),
        _ => "Status update",
    };

    /// <summary>
    /// What a failed row says.
    /// </summary>
    /// <remarks>
    /// A bare "Failed" leaves the reader with nothing to act on, and the row already carries
    /// the reason in <see cref="SafeCode"/>. The code is a stable machine token, so it is
    /// translated here rather than shown: `prompt_contract_invalid` is not a sentence.
    /// An unrecognised code falls back to the token with its hyphens opened out, which is
    /// still more than the word "Failed" says.
    /// </remarks>
    public string FailedText => Kind switch
    {
        // The run stopped part way; the call itself was never answered.
        _ when SafeCode is "interrupted" => "Stopped before finishing",
        _ when SafeCode is "model_failed" => "Model call failed",
        _ when SafeCode is "prompt_contract_invalid" => "Prompt rejected by the runtime",
        _ when SafeCode is "cancelled" => "Cancelled",
        _ when SafeCode is "transport_failure" => "Lost connection to the tool",
        _ when SafeCode is "reconciliation_required" => "Stopped: the last change is unverified",
        _ when SafeCode is { Length: > 0 } code => "Failed: " + code.Replace('_', ' '),
        _ => "Failed",
    };

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

            // Bounded machine codes leave the headline for the muted line.
            if ((State == AgentActivityState.Waiting || State == AgentActivityState.Failed)
                && !string.IsNullOrWhiteSpace(SafeCode))
            {
                parts.Add(SafeCode);
            }

            return string.Join(" · ", parts);
        }
    }

    public bool HasDuration => DurationShortText != null;

    /// <summary>Glanceable duration chip for settled non-thinking rows.
    /// Thinking rows already spell the duration out in words.</summary>
    public string? DurationShortText
    {
        get
        {
            if (Kind == AgentActivityKind.Thinking || EffectiveDurationMs is not { } ms)
            {
                return null;
            }

            return ms < 1000 ? $"{ms}ms" : $"{ms / 1000d:0.#}s";
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

    /// <summary>
    /// Applies a newer frame for the same activity id. A stale <c>Started</c>
    /// frame arriving after the row already settled (reorder or retry) is
    /// ignored so a finished row can never resurrect. Returns false when skipped.
    /// </summary>
    public bool UpdateFrom(AgentActivityModel activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (!string.Equals(ActivityId, activity.ActivityId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Activity id cannot change.", nameof(activity));
        }

        if (activity.State == AgentActivityState.Started
            && State is AgentActivityState.Completed or AgentActivityState.Failed)
        {
            return false;
        }

        Apply(activity);
        return true;
    }

    /// <summary>
    /// True when the row was settled by <see cref="SettleInterruptedRun"/> instead
    /// of a real completion frame. Deferred terminal frames must not overwrite it.
    /// </summary>
    public bool IsSettledInterruption =>
        State == AgentActivityState.Failed
        && string.Equals(SafeCode, "interrupted", StringComparison.Ordinal);

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
        // Producer only sends @@ old ==> new on Started; Completed carries
        // Detail=null. Keep the preview so a fast edit stays visible after settle.
        if (!string.IsNullOrWhiteSpace(activity.Detail) || !IsEditPreview)
        {
            Detail = activity.Detail;
        }

        // The producer replaces the list wholesale, so the newest activity carrying one
        // is the whole truth and there is nothing to merge here.
        Todos = activity.Todos is { Count: > 0 } rows
            ? rows.Select(row => new TodoItemModel(row.Content, row.Status)).ToArray()
            : null;
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
        OnPropertyChanged(nameof(HasDuration));
        OnPropertyChanged(nameof(DurationShortText));
        OnPropertyChanged(nameof(HasLineDelta));
        OnPropertyChanged(nameof(HasNoLineDelta));
        OnPropertyChanged(nameof(HasLineDeltaAndBaseMetadata));
        OnPropertyChanged(nameof(AddedDeltaText));
        OnPropertyChanged(nameof(RemovedDeltaText));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(EffectiveDurationMs));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(IsEditPreview));
        OnPropertyChanged(nameof(HasEditPreview));
        OnPropertyChanged(nameof(EditBeforeText));
        OnPropertyChanged(nameof(EditAfterText));
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
            return "Thought briefly";
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

    private (string Before, string After) SplitEditPreview()
    {
        var detail = Detail ?? string.Empty;
        var body = detail.StartsWith("@@ ", StringComparison.Ordinal) ? detail[3..] : detail;
        var separator = body.IndexOf(" ==> ", StringComparison.Ordinal);
        if (separator < 0)
        {
            return (UnescapePreview(body), string.Empty);
        }

        return (
            UnescapePreview(body[..separator]),
            UnescapePreview(body[(separator + 5)..]));
    }

    private static string UnescapePreview(string value) =>
        value.Replace("\\n", "\n", StringComparison.Ordinal);

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
