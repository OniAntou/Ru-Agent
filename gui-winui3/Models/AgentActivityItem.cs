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
        private set
        {
            if (SetProperty(ref _fileName, value))
            {
                OnPropertyChanged(nameof(HasFileName));
            }
        }
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

    private bool _detailRepeats;

    /// <summary>
    /// Marks this row's detail as a repeat of the row above.
    /// </summary>
    /// <remarks>
    /// A model that reuses the same deliberation across consecutive steps produces
    /// one Thinking row per step, and every one of them renders the same
    /// paragraph. The steps themselves stay -- their durations are what tells the
    /// reader where the time went -- but the paragraph is read once. The value
    /// itself is left alone: it is still what the row carries.
    /// </remarks>
    internal void SetDetailRepeated(bool repeated)
    {
        if (_detailRepeats == repeated)
        {
            return;
        }

        _detailRepeats = repeated;
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(ShowThinkingDetail));
        OnPropertyChanged(nameof(ShowPlainDetail));
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
    ///
    /// Thinking rows never use the plain line: their detail lives in the collapsible
    /// quote block below, so a long deliberation does not push the answer off screen.
    /// </summary>
    public bool HasDetail => !_detailRepeats && !string.IsNullOrWhiteSpace(Detail) && !IsEditPreview;

    public bool IsThinking => Kind == AgentActivityKind.Thinking;

    public bool HasThinkingDetail => IsThinking && HasDetail;

    /// <summary>
    /// The header is a toggle only for rows that have something to fold away. Every
    /// other row keeps the plain label, and a second clickable header would leave the
    /// row looking interactive without being so.
    /// </summary>
    public bool ShowPlainHeader => !HasThinkingDetail && !HasEditPreview;

    public bool ShowPlainDetail => HasDetail
        && !IsThinking
        && Kind is not (AgentActivityKind.Terminal or AgentActivityKind.Narration);

    public bool ShowThinkingDetail => HasThinkingDetail && IsThinkingExpanded;

    private bool _isThinkingExpanded;

    /// <summary>
    /// Whether the thinking quote block is open. Closed by default: thinking is
    /// metadata about the turn, and an always-open deliberation buries the reply
    /// it produced. Toggled from the row header; never persisted.
    /// </summary>
    public bool IsThinkingExpanded
    {
        get => _isThinkingExpanded;
        set
        {
            if (SetProperty(ref _isThinkingExpanded, value))
            {
                OnPropertyChanged(nameof(ShowThinkingDetail));
                OnPropertyChanged(nameof(ThinkingChevronGlyph));
            }
        }
    }

    /// <summary>Chevron for the thinking toggle: down when open, right when closed.</summary>
    public string ThinkingChevronGlyph => IsThinkingExpanded ? "\uE70D" : "\uE76C";

    /// <summary>Two-pane edit preview marker: "@@ old ==&gt; new" with escaped newlines.</summary>
    public bool IsEditPreview =>
        Kind == AgentActivityKind.Edit
        && Detail is { } detail
        && detail.StartsWith("@@ ", StringComparison.Ordinal)
        && detail.Contains(" ==> ", StringComparison.Ordinal);

    public bool HasEditPreview => IsEditPreview;

    private bool _isEditPreviewExpanded;

    /// <summary>
    /// Whether the diff is open. A row starts folded: a diff nobody asked to see is the
    /// bulkiest thing the feed can carry, and the header already names the file it belongs
    /// to, so the panel is one click away rather than always in the way. The row owns this
    /// because the row header is the toggle: while the header lived inside the preview, the
    /// only way back to a folded diff was a chevron stranded on the panel it had just closed.
    /// </summary>
    public bool IsEditPreviewExpanded
    {
        get => _isEditPreviewExpanded;
        set
        {
            if (SetProperty(ref _isEditPreviewExpanded, value))
            {
                OnPropertyChanged(nameof(ShowEditPreview));
                OnPropertyChanged(nameof(ShowTerminalOutput));
                OnPropertyChanged(nameof(EditPreviewChevronGlyph));
            }
        }
    }

    /// <summary>The diff itself, drawn only while it is open.</summary>
    public bool ShowEditPreview => HasEditPreview && IsEditPreviewExpanded;

    /// <summary>
    /// The command's output as it arrives, bounded by the terminal panel's own policy.
    /// </summary>
    /// <remarks>
    /// A terminal row is the one place the feed can show what a command actually printed.
    /// The text comes from the same blocks the terminal panel appends to, so the two cannot
    /// disagree, and it is filled while the command runs: a row that has settled keeps
    /// whatever it received, which is the honest limit of a live stream.
    /// </remarks>
    public string TerminalOutputText => _terminalOutputText;

    private string _terminalOutputText = string.Empty;

    /// <summary>
    /// What the open panel shows: the command with a shell prompt, then its output.
    /// </summary>
    /// <remarks>
    /// The panel repeats the command on purpose: by the time a reader opens it the header may
    /// have scrolled away, and a wall of output with no command above it is the thing the
    /// panel exists to prevent. The prompt is a plain "$" rather than the real one, because
    /// the harness runs the command through PowerShell, not through a shell the reader chose.
    /// </remarks>
    public string TerminalPanelText => HasTerminalOutput
        ? $"$ {Detail}\n\n{TerminalOutputText}"
        : string.Empty;

    public bool HasTerminalOutput => _terminalOutputText.Length > 0;

    public bool ShowTerminalOutput => HasTerminalOutput && IsEditPreviewExpanded;

    /// <summary>
    /// Whether the row's header folds something: a diff, an output block, or both.
    /// </summary>
    /// <remarks>
    /// The fold state lives in <see cref="IsEditPreviewExpanded"/> for both panels on
    /// purpose: a row has one detail, and a reader who opened one of them meant to read
    /// the row, not one specific pane.
    /// </remarks>
    public bool ShowRowDetailToggle => HasEditPreview || HasTerminalOutput;

    /// <summary>
    /// Appends a chunk of the running command's output, bounded the way the terminal
    /// panel bounds its blocks so a runaway command cannot grow a row without limit.
    /// </summary>
    public void AppendTerminalOutput(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        string combined = _terminalOutputText + chunk;
        if (combined.Length > TerminalOutputBlock.MaximumCharacters)
        {
            combined = combined[^TerminalOutputBlock.MaximumCharacters..];
        }

        if (string.Equals(combined, _terminalOutputText, StringComparison.Ordinal))
        {
            return;
        }

        bool hadOutput = _terminalOutputText.Length > 0;
        _terminalOutputText = combined;
        OnPropertyChanged(nameof(TerminalOutputText));
        OnPropertyChanged(nameof(TerminalPanelText));
        OnPropertyChanged(nameof(ShowTerminalOutput));
        if (!hadOutput)
        {
            OnPropertyChanged(nameof(HasTerminalOutput));
            OnPropertyChanged(nameof(ShowRowDetailToggle));
        }
    }

    /// <summary>
    /// Drops the chunk text a superseded attempt streamed into this row, so the
    /// retried attempt's output is the only thing the row shows.
    /// </summary>
    public void ResetTerminalOutput()
    {
        if (_terminalOutputText.Length == 0)
        {
            return;
        }

        _terminalOutputText = string.Empty;
        OnPropertyChanged(nameof(TerminalOutputText));
        OnPropertyChanged(nameof(TerminalPanelText));
        OnPropertyChanged(nameof(ShowTerminalOutput));
        OnPropertyChanged(nameof(HasTerminalOutput));
        OnPropertyChanged(nameof(ShowRowDetailToggle));
    }

    /// <summary>Chevron for the edit toggle: down when open, right when closed.</summary>
    public string EditPreviewChevronGlyph => IsEditPreviewExpanded ? "\uE70D" : "\uE76C";

    public string EditBeforeText => IsEditPreview ? SplitEditPreview().Before : string.Empty;

    public string EditAfterText => IsEditPreview ? SplitEditPreview().After : string.Empty;

    private bool _isVisibleInFeed = true;

    /// <summary>Feed grouping collapses redundant rows; collapsed rows keep
    /// their data for history but leave the visible feed.</summary>
    /// <remarks>
    /// A status row that says only "ok" is not shown at all. It is the one kind of row
    /// whose entire job is to explain something, and "ok" explains nothing: the step
    /// around it either produced a row of its own or is visible in its absence. The row
    /// still exists and keeps its data; it just never earns a line in the feed.
    /// </remarks>
    public bool IsVisibleInFeed
    {
        get => _isVisibleInFeed && !IsSilentStatus;
        set => SetProperty(ref _isVisibleInFeed, value);
    }

    /// <summary>
    /// The command as one line, for the header that has to carry it.
    /// </summary>
    /// <remarks>
    /// A command can be a whole pipeline. The row names what ran, so the first line is
    /// enough; the output and the rest of the command live in the terminal panel.
    /// </remarks>
    private static string? CommandHeadline(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        string line = detail.Trim().Split('\n')[0].Trim();
        return line.Length <= 120 ? line : line[..120] + "…";
    }

    /// <summary>Whether this row's only content is the word for success.</summary>
    private bool IsSilentStatus =>
        Kind == AgentActivityKind.Status && SafeCode is "ok" or "succeeded";

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
        AgentActivityKind.Terminal => CommandHeadline(Detail) is { } running ? $"Running {running}" : "Running terminal task",
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
        AgentActivityKind.Terminal => CommandHeadline(Detail) is { } ran ? $"Ran {ran}" : "Ran terminal task",
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
    /// A tool row keeps the wording it had while it ran. The row was already the
    /// subject — its label names the action and its pill names the file — and failure
    /// changes the state, which is what the colour is for. Putting a failure sentence
    /// in the label's place renamed the row at the exact moment the reader was
    /// tracking it: "Writing" beside script.js became "Failed: policy denied", and the
    /// file that failed went unmentioned.
    ///
    /// Status and Thinking rows are the exception. Neither names an action, and both
    /// exist to report what happened: for them the reason *is* the report, and losing
    /// it would leave a cancelled or unbuildable model call saying nothing at all.
    /// </remarks>
    public string FailedText => Kind switch
    {
        AgentActivityKind.Status or AgentActivityKind.Thinking => FailureSentence(SafeCode),
        // Terminal is the one tool row whose running wording cannot survive the process:
        // "Running <command>" stops being true the moment the command exits, so a settled
        // one says "Ran <command>" and lets the colour carry how it went. It also means a
        // row the run settled without a terminal frame stops claiming to still be running,
        // which is how two rows for one command used to appear.
        AgentActivityKind.Terminal => CompletedText,
        _ => StartedText,
    };

    /// <summary>
    /// The sentence for a row that exists to explain itself, by reason.
    ///
    /// A bare "Failed" leaves the reader with nothing to act on. The code is a stable
    /// machine token, so it is translated here rather than shown: `prompt_contract_invalid`
    /// is not a sentence. An unrecognised code falls back to the token with its
    /// underscores opened out, which is still more than the word "Failed" says.
    /// </summary>
    private static string FailureSentence(string? safeCode) => safeCode switch
    {
        // The run stopped part way; the call itself was never answered.
        "interrupted" => "Stopped before finishing",
        "model_failed" => "Model call failed",
        "prompt_contract_invalid" => "Prompt rejected by the runtime",
        "cancelled" => "Cancelled",
        "transport_failure" => "Lost connection to the tool",
        "reconciliation_required" => "Stopped: the last change is unverified",
        { Length: > 0 } code => "Failed: " + code.Replace('_', ' '),
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

            // A waiting headline never names its reason, so the muted line keeps
            // the machine code. A failed headline already translated it, so the
            // code would only repeat noise; the copy action carries it instead.
            if (State == AgentActivityState.Waiting
                && !string.IsNullOrWhiteSpace(SafeCode))
            {
                parts.Add(SafeCode);
            }

            return string.Join(" · ", parts);
        }
    }

    public bool HasFileName => !string.IsNullOrWhiteSpace(FileName);

    /// <summary>
    /// Whether the metadata pill has anything to show. The pill wraps the whole
    /// cluster so an empty one never renders as a bare rounded box.
    /// </summary>
    public bool HasMetadata => !string.IsNullOrWhiteSpace(MetadataText);

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

            return ms < 1000
                ? $"{ms}ms"
                : (ms / 1000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "s";
        }
    }

    public string DisplayText => Kind == AgentActivityKind.Narration
        ? NarrationText
        : State switch
    {
        AgentActivityState.Started => StartedText,
        AgentActivityState.Completed => CompletedText,
        AgentActivityState.Waiting => WaitingText,
        AgentActivityState.Failed => FailedText,
        _ => "Working…",
    };

    /// <summary>
    /// What the model said while it worked, as the row's own text.
    /// </summary>
    /// <remarks>
    /// The sentence <em>is</em> the row: narration is not a label describing an action, so it
    /// carries no verb prefix and no metadata — a reader scanning the feed reads what the
    /// model said, the same way they read a reply.
    /// </remarks>
    private string NarrationText => string.IsNullOrWhiteSpace(Detail) ? "Speaking" : Detail!;

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

        // A row settled by SettleInterruptedRun keeps that outcome: a deferred
        // frame arriving after the run ended must not turn the interruption into
        // a completion, and must not replace its wording.
        if (IsSettledInterruption)
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

        OnPropertyChanged(nameof(IsVisibleInFeed));
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
        OnPropertyChanged(nameof(HasFileName));
        OnPropertyChanged(nameof(HasMetadata));
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
        OnPropertyChanged(nameof(IsThinking));
        OnPropertyChanged(nameof(HasThinkingDetail));
        OnPropertyChanged(nameof(ShowPlainHeader));
        OnPropertyChanged(nameof(ShowPlainDetail));
        OnPropertyChanged(nameof(ShowThinkingDetail));
        OnPropertyChanged(nameof(ThinkingChevronGlyph));
        OnPropertyChanged(nameof(IsEditPreview));
        OnPropertyChanged(nameof(HasEditPreview));
        OnPropertyChanged(nameof(IsEditPreviewExpanded));
        OnPropertyChanged(nameof(ShowEditPreview));
        OnPropertyChanged(nameof(EditPreviewChevronGlyph));
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
