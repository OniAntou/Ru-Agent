namespace RuAgent.Cli.Tui;

public enum TuiRunState
{
    Idle,
    Thinking,
    Tool,
    WaitingApproval,
    WaitingTrust,
    Paused,
    Failed,
}

/// <summary>
/// The one mutable row above the composer. It owns the spinner, the current activity and the
/// counters, so no other component needs to repaint in place or clear its own leftovers.
/// </summary>
public sealed class TuiStatusLine
{
    private static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private readonly TuiSurface _surface;
    private int _frame;
    private TuiRunState _state = TuiRunState.Idle;
    private string? _activity;
    private string? _activityAction;
    private string? _activityTarget;
    private string? _detail;
    private double _elapsedSeconds;
    private long? _tokens;
    private int _tools;
    private bool _responding;

    public TuiStatusLine(TuiSurface surface)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
    }

    public TuiRunState State => _state;

    /// <summary>Current braille spinner frame, reused by the composer hint row.</summary>
    public string SpinnerFrame => Frames[_frame];

    /// <summary>Elapsed seconds of the turn in progress.</summary>
    public double ElapsedSeconds => _elapsedSeconds;

    /// <summary>True while this row owns the current terminal line.</summary>
    public bool IsLive { get; set; }

    /// <summary>
    /// True when the row is painted inside the sticky footer instead of at the transcript
    /// cursor, so transcript appends must not repaint it.
    /// </summary>
    public bool InFooter { get; set; }

    public void SetState(TuiRunState state)
    {
        _state = state;
        _responding = false;
    }

    public void SetResponding() => _responding = true;

    public void SetActivity(string? activity)
    {
        _activity = activity;
        _activityAction = activity;
        _activityTarget = null;
    }

    public void SetActivity(string action, string? target)
    {
        _activityAction = action;
        _activityTarget = target;
        _activity = string.IsNullOrWhiteSpace(target) ? action : action + " " + target;
    }

    public TuiActivityPresentation Presentation(int width)
    {
        var (glyph, label, tone) = _state switch
        {
            TuiRunState.Thinking => (SpinnerFrame, _responding ? "Writing response" : "Thinking", TuiToken.Brand),
            TuiRunState.Tool => (SpinnerFrame, string.IsNullOrWhiteSpace(_activityAction) ? "Running tool" : _activityAction, TuiToken.Brand),
            TuiRunState.WaitingApproval => ("?", "Approval needed", TuiToken.Warn),
            TuiRunState.WaitingTrust => ("?", "Workspace needs trust", TuiToken.Warn),
            TuiRunState.Paused => ("Ⅱ", "Paused", TuiToken.Warn),
            TuiRunState.Failed => ("✗", "Run failed", TuiToken.Err),
            _ => ("·", "Ready", TuiToken.Meta),
        };
        label = char.ToUpperInvariant(label[0]) + label[1..];
        var metrics = new List<string>();
        if (_state is TuiRunState.Thinking or TuiRunState.Tool || _elapsedSeconds > 0)
            metrics.Add(FormatElapsed(_elapsedSeconds));
        if (_tools > 0 && width >= 64) metrics.Add(_tools == 1 ? "1 tool" : $"{_tools} tools");
        var target = _state == TuiRunState.Tool ? _activityTarget
            : _state is TuiRunState.WaitingTrust or TuiRunState.Paused or TuiRunState.Idle ? _detail : null;
        if (_state is TuiRunState.WaitingApproval or TuiRunState.WaitingTrust or TuiRunState.Paused)
            metrics.Clear();
        return new(glyph, label, target, string.Join(" · ", metrics), tone);
    }

    public void SetDetail(string? detail) => _detail = detail;

    public void SetElapsed(double seconds) => _elapsedSeconds = seconds < 0 ? 0 : seconds;

    public void SetCounters(long? tokens, int tools)
    {
        _tokens = tokens;
        _tools = tools;
    }

    public void AddTool() => _tools++;

    public void Tick() => _frame = (_frame + 1) % Frames.Length;

    public void Reset()
    {
        _state = TuiRunState.Idle;
        _activity = null;
        _activityAction = null;
        _activityTarget = null;
        _detail = null;
        _elapsedSeconds = 0;
        _tokens = null;
        _tools = 0;
        _responding = false;
    }

    /// <summary>
    /// Serializes the live-row check and the paint against <see cref="Clear"/>:
    /// a ticker that read <see cref="IsLive"/> just before a stop must not paint
    /// over the row the stop cleared.
    /// </summary>
    private readonly object _liveGate = new();

    public void Render(int width)
    {
        lock (_liveGate)
        {
            if (!IsLive || InFooter)
            {
                return;
            }

            _surface.RepaintSingleLine(Compose(width), width);
        }
    }

    public void Clear(int width)
    {
        lock (_liveGate)
        {
            IsLive = false;
            _surface.ClearSingleLine(width);
        }
    }

    /// <summary>The composed row to repaint after a transcript block, or null when the row is idle.</summary>
    public string? LiveRow(int width) => IsLive && !InFooter ? Compose(width) : null;

    /// <summary>Pure composition of the live row; every line is exactly <paramref name="width"/> cells.</summary>
    public string Compose(int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        const string Prefix = "  ";
        const string Suffix = "  ";
        var inner = Math.Max(1, width - Prefix.Length - Suffix.Length);
        var left = ComposeLeft();
        var right = ComposeRight(width);
        var gap = 2;
        string body;
        if (right.Length == 0)
        {
            body = TuiText.Pad(left, inner);
        }
        else
        {
            var leftWidth = TuiText.VisibleWidth(left);
            var rightWidth = TuiText.VisibleWidth(right);
            body = leftWidth + rightWidth + gap <= inner
                ? left + new string(' ', inner - leftWidth - rightWidth) + right
                : TuiText.Pad(left, inner);
        }

        return Prefix + body + Suffix;
    }

    private string ComposeLeft()
    {
        var frame = Frames[_frame];
        switch (_state)
        {
            case TuiRunState.Thinking:
                return TuiPalette.Paint(frame, TuiToken.Brand)
                    + " "
                    + TuiPalette.Paint("Thinking", TuiToken.Meta)
                    + TuiPalette.Paint(" · " + FormatElapsed(_elapsedSeconds), TuiToken.Line);
            case TuiRunState.Tool:
                var label = string.IsNullOrWhiteSpace(_activity) ? "Running tool" : _activity;
                return TuiPalette.Paint(frame, TuiToken.Brand)
                    + " "
                    + label
                    + TuiPalette.Paint(" · " + FormatElapsed(_elapsedSeconds), TuiToken.Line);
            case TuiRunState.WaitingApproval:
                return TuiPalette.Paint("▲ ", TuiToken.Warn) + TuiPalette.Paint("approval needed", TuiToken.Warn);
            case TuiRunState.WaitingTrust:
                // No work is in flight while the workspace-trust challenge is open, so the
                // row must not spin. A spinner here read as a slow model when the run was
                // in fact parked until a human answered, and the elapsed clock kept counting
                // toward a turn that could never start.
                var trust = TuiPalette.Paint("▲ ", TuiToken.Warn) + TuiPalette.Paint("workspace needs trust", TuiToken.Warn);
                if (!string.IsNullOrWhiteSpace(_detail))
                {
                    trust += TuiPalette.Paint(" · " + _detail, TuiToken.Line);
                }

                return trust;
            case TuiRunState.Paused:
                // A parked run has no work in flight: the server holds the run until an
                // explicit Continue. Spinning here read as a slow model over a run that
                // was in fact waiting on the operator, so the row names the pause and
                // the resume path instead.
                var paused = TuiPalette.Paint("▲ ", TuiToken.Warn) + TuiPalette.Paint("run paused", TuiToken.Warn);
                if (!string.IsNullOrWhiteSpace(_detail))
                {
                    paused += TuiPalette.Paint(" · " + _detail, TuiToken.Line);
                }

                return paused;
            case TuiRunState.Failed:
                return TuiPalette.Paint("✗ ", TuiToken.Err) + TuiPalette.Paint("last run failed", TuiToken.Err);
            default:
                var idle = TuiPalette.Paint("✓ ", TuiToken.Ok) + TuiPalette.Paint("idle", TuiToken.Meta);
                if (_elapsedSeconds > 0)
                {
                    idle += TuiPalette.Paint(" · " + FormatElapsed(_elapsedSeconds), TuiToken.Line);
                }

                if (!string.IsNullOrWhiteSpace(_detail))
                {
                    idle += TuiPalette.Paint(" · " + _detail, TuiToken.Line);
                }

                return idle;
        }
    }

    private string ComposeRight(int width)
    {
        var parts = new List<string>();
        if (_tokens is { } tokens)
        {
            parts.Add("⇣" + FormatTokens(tokens));
        }

        if (_tools > 0)
        {
            parts.Add(_tools == 1 ? "1 tool" : $"{_tools} tools");
        }

        if (parts.Count == 0)
        {
            return _state == TuiRunState.Idle ? TuiPalette.Paint("/help", TuiToken.Line) : string.Empty;
        }

        // Below 60 columns the counters are dropped rather than crowding the activity label.
        return width < 60 ? string.Empty : TuiPalette.Paint(string.Join(" · ", parts), TuiToken.Line);
    }

    public static string FormatElapsed(double seconds)
    {
        if (seconds < 60)
        {
            return seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s";
        }

        var whole = (int)seconds;
        return $"{whole / 60}m{whole % 60:00}s";
    }

    public static string FormatTokens(long tokens)
    {
        return RuAgent.SessionInterchange.TranscriptText.FormatTokens(tokens);
    }
}
