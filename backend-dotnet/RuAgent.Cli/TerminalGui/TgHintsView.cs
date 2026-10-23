using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>One-line run status and responsive keyboard hints for the chat footer.</summary>
public sealed class TgHintsView : View
{
    private TgHintsState _activeState;
    private TgHintsState _stateBeforeApproval;
    private int _queuedCount;
    private long _waveBucket;
    private double _waveSeconds;
    private const int BusyBarCells = 5;
    private const string BusyBarGlyph = "▬";
    private string _idleLabel = "Ready";
    private string? _busyHint;
    private string? _feedbackMessage;
    private bool _feedbackIsError;
    private bool _hasUnseenTranscript;
    private bool _scrolledBack;
    private string? _busyRunId;
    private string? _busyStatus;

    public TgHintsView()
    {
        Height = 1;
        SetScheme(TgPalette.Scheme(TuiToken.Meta));
    }

    public string Workspace { get; private set; } = string.Empty;
    public bool Compact { get; set; }
    public TgHintsState ActiveState => _activeState;
    public int QueuedCount => _queuedCount;
    public bool IsRunning => _activeState == TgHintsState.Running;
    public string? FeedbackMessage => _feedbackMessage;
    public bool FeedbackIsError => _feedbackIsError;
    public string StatusText => TgStatusPresentation.StatusText(
        _activeState,
        _queuedCount,
        _feedbackMessage,
        _feedbackIsError,
        _busyHint,
        _idleLabel);
    public event Action? WorkspaceChanged;

    public void SetIdle(string workspace)
    {
        Workspace = ChatDisplayText.Sanitize(workspace, 200);
        _idleLabel = TgStatusPresentation.IdleLabel(Workspace);
        WorkspaceChanged?.Invoke();
        _activeState = TgHintsState.Idle;
        _queuedCount = 0;
        _waveBucket = 0;
        _busyHint = null;
        ClearFeedback();
        SetNeedsDraw();
    }

    /// <summary>
    /// Swaps the left text for the colour-wave busy indicator. An optional hint replaces
    /// the default "esc interrupt" copy, e.g. while queued lines wait behind the active
    /// turn.
    /// </summary>
    public void SetBusy(string? hint = null)
    {
        _activeState = TgHintsState.Running;
        _busyHint = hint;
        ClearFeedback();
        SetNeedsDraw();
    }

    public void SetRunning(int queuedCount)
    {
        _activeState = TgHintsState.Running;
        _queuedCount = Math.Max(0, queuedCount);
        _busyHint = null;
        ClearFeedback();
        SetNeedsDraw();
    }

    public void SetPaused(int queuedCount)
    {
        _activeState = TgHintsState.Paused;
        _queuedCount = Math.Max(0, queuedCount);
        _busyHint = null;
        ClearFeedback();
        SetNeedsDraw();
    }

    public void SetWaitingForApproval(bool waiting)
    {
        if (waiting)
        {
            if (_activeState != TgHintsState.WaitingForApproval)
            {
                _stateBeforeApproval = _activeState;
                _activeState = TgHintsState.WaitingForApproval;
            }
        }
        else if (_activeState == TgHintsState.WaitingForApproval)
        {
            // Approval pauses the current run; resolving it restores that state and keeps
            // the queue count until the loop reports its next explicit state.
            _activeState = _stateBeforeApproval;
        }
        else return;

        _busyHint = null;
        ClearFeedback();
        SetNeedsDraw();
    }

    public void SetQueueCount(int queuedCount)
    {
        _queuedCount = Math.Max(0, queuedCount);
        SetNeedsDraw();
    }

    public void ShowFeedback(string message, bool isError = false)
    {
        _feedbackMessage = string.IsNullOrWhiteSpace(message)
            ? null
            : ChatDisplayText.Sanitize(message, 200);
        _feedbackIsError = _feedbackMessage is not null && isError;
        SetNeedsDraw();
    }

    public void ClearFeedback()
    {
        _feedbackMessage = null;
        _feedbackIsError = false;
        SetNeedsDraw();
    }

    /// <summary>
    /// Advances the busy wave from the shared animation clock; the phase is a pure
    /// function of elapsed seconds. A no-op while the row is idle or while the phase
    /// bucket has not moved, so an 18 ms ticker repaints about sixteen times a second.
    /// </summary>
    public void SetWaveClock(double elapsedSeconds)
    {
        if (!IsRunning)
        {
            return;
        }

        var bucket = TuiSpinner.WaveBucketAt(elapsedSeconds);
        if (bucket == _waveBucket)
        {
            return;
        }

        _waveBucket = bucket;
        _waveSeconds = elapsedSeconds;
        SetNeedsDraw();
    }

    public void SetTranscriptUnread(bool hasUnseenTranscript)
    {
        _hasUnseenTranscript = hasUnseenTranscript;
        SetNeedsDraw();
    }

    public void SetWorkspaceBusy(string runId, string status)
    {
        var safeRunId = ChatDisplayText.Sanitize(runId, 64);
        var safeStatus = ChatDisplayText.Sanitize(status, 24);
        if (_busyRunId == safeRunId && _busyStatus == safeStatus) return;
        _busyRunId = safeRunId;
        _busyStatus = safeStatus;
        SetNeedsDraw();
    }

    public void ClearWorkspaceBusy()
    {
        if (_busyRunId is null) return;
        _busyRunId = null;
        _busyStatus = null;
        SetNeedsDraw();
    }

    public void SetTranscriptFollowing(bool followingTail)
    {
        _scrolledBack = !followingTail;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        if (_busyRunId is not null)
        {
            Draw(0, [($"blocked ({_busyStatus}) · ru agent cancel {_busyRunId}", TuiToken.Warn)], width);
            return true;
        }
        var rightHints = GetRightHints(width);
        var rightWidth = TuiText.VisibleWidth(rightHints);
        // Preserve room for the right-side key hints, then give the rest to status text.
        var leftBudget = Math.Max(0, width - rightWidth - 2);
        var wavePrefixWidth = IsRunning
            ? BusyBarCells + 1
            : 0;
        var leftText = StatusText;
        if (_scrolledBack) leftText = "↑ scrolled back · " + leftText;
        var shownLeft = TuiText.Truncate(leftText, Math.Max(0, leftBudget - wavePrefixWidth));
        var leftWidth = Math.Min(leftBudget, TuiText.VisibleWidth(shownLeft) + wavePrefixWidth);
        var gap = Math.Max(1, width - leftWidth - rightWidth);

        DrawStatus(leftWidth, shownLeft);
        DrawRight(leftWidth + gap, Math.Max(0, width - leftWidth - gap));
        return true;
    }

    private void DrawStatus(int maxWidth, string shownText)
    {
        if (_scrolledBack)
        {
            Draw(0, [(shownText, TuiToken.Meta)], maxWidth);
            return;
        }

        if (_feedbackMessage is not null)
        {
            var token = _feedbackIsError ? TuiToken.Err : TuiToken.Meta;
            if (IsRunning)
            {
                var feedbackColumn = DrawWave(0, maxWidth);
                Draw(feedbackColumn, [(" ", TuiToken.Line)], maxWidth - feedbackColumn);
                feedbackColumn++;
                Draw(feedbackColumn, [(shownText, token)], maxWidth - feedbackColumn);
            }
            else
            {
                Draw(0, [(shownText, token)], maxWidth);
            }
            return;
        }

        if (!IsRunning)
        {
            var token = _activeState is TgHintsState.WaitingForApproval or TgHintsState.Paused
                ? TuiToken.Warn
                : _queuedCount > 0 ? TuiToken.Brand : TuiToken.Meta;
            Draw(0, [(shownText, token)], maxWidth);
            return;
        }

        var column = DrawWave(0, maxWidth);
        var rest = shownText;
        if (rest.Length == 0)
        {
            return;
        }

        Draw(column, [(" ", TuiToken.Line)], maxWidth - column);
        column++;
        IReadOnlyList<(string Text, TuiToken Token)> segments = _busyHint == "esc interrupt"
            ? [("esc", TuiToken.Bright), (rest.Length > 3 ? rest[3..] : string.Empty, TuiToken.Meta)]
            : [(rest, TuiToken.Meta)];
        Draw(column, segments, maxWidth - column);
    }

    /// <summary>Draws the wave dots with their per-cell ramp colours; returns the column after them.</summary>
    private int DrawWave(int column, int maxWidth)
    {
        for (var cell = 0; cell < BusyBarCells; cell++)
        {
            if (maxWidth <= 0 || column >= Viewport.Width)
            {
                break;
            }

            var phase = _waveSeconds + TuiSpinner.WaveCycleSeconds
                - cell * TuiSpinner.WaveCycleSeconds / BusyBarCells;
            SetAttribute(TgPalette.Attr(TuiSpinner.WaveRgbAt(0, phase)));
            Move(column, 0);
            AddStr(BusyBarGlyph);
            column++;
            maxWidth--;
        }

        return column;
    }

    /// <summary>Bright key caps with meta words.</summary>
    private void DrawRight(int column, int maxWidth)
    {
        Draw(column, GetRightHintSegments(Viewport.Width), maxWidth);
    }

    private string GetRightHints(int width)
    {
        var keys = string.Join("  ", VisibleKeyHints(width));
        return _hasUnseenTranscript && !Compact && width >= 80
            ? "↓ new output  ctrl+end tail  " + keys
            : keys;
    }

    private IReadOnlyList<(string Text, TuiToken Token)> GetRightHintSegments(int width)
    {
        var segments = new List<(string Text, TuiToken Token)>();
        if (_hasUnseenTranscript && !Compact && width >= 80)
        {
            segments.Add(("↓ new output", TuiToken.Brand));
            segments.Add(("  ctrl+end", TuiToken.Bright));
            segments.Add((" tail", TuiToken.Meta));
        }

        foreach (var hint in VisibleKeyHints(width))
        {
            if (segments.Count > 0)
            {
                segments.Add(("  ", TuiToken.Line));
            }

            var separator = hint.IndexOf(' ');
            if (separator <= 0)
            {
                segments.Add((hint, TuiToken.Bright));
                continue;
            }

            segments.Add((hint[..separator], TuiToken.Bright));
            segments.Add((hint[separator..], TuiToken.Meta));
        }

        return segments;
    }

    private IReadOnlyList<string> VisibleKeyHints(int width) =>
        IsRunning
            ? ["esc interrupt"]
            : Compact
                ? TgStatusPresentation.KeyHints(width, compact: true)
                : width < 128 && _hasUnseenTranscript
                    ? ["ctrl+p commands"]
                    : TgStatusPresentation.KeyHints(width, compact: false);

    private void Draw(int column, IReadOnlyList<(string Text, TuiToken Token)> segments, int maxWidth)
    {
        foreach (var (text, token) in segments)
        {
            if (maxWidth <= 0 || column >= Viewport.Width)
            {
                break;
            }

            var clipped = TuiText.Truncate(text, maxWidth);
            if (clipped.Length == 0)
            {
                break;
            }

            SetAttribute(TgPalette.Attr(token));
            Move(column, 0);
            AddStr(clipped);
            var drawn = TuiText.VisibleWidth(clipped);
            column += drawn;
            maxWidth -= drawn;
            if (drawn < TuiText.VisibleWidth(text))
            {
                break;
            }
        }
    }
}
