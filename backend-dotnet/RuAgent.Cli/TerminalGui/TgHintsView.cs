using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Hint row: idle workspace left, key hints right; busy swaps in the spinner.</summary>
public sealed class TgHintsView : View
{
    private static readonly string KeysRight = string.Concat(ComposerBox.HintKeySegments.Select(segment => segment.Text));
    private static readonly string ExtendedKeysRight = KeysRight + "  ctrl+p palette  pgup/dn scroll  ctrl+end tail";
    private static readonly string DetachedKeysRight = "↓ new output  ctrl+end tail  " + KeysRight;
    private static readonly string ExtendedDetachedKeysRight = DetachedKeysRight + "  ctrl+p palette  pgup/dn scroll";
    private const int ExtendedHintsMinimumWidth = 128;

    /// <summary>The busy indicator's visible text: the wave dots, then the interrupt copy.</summary>
    private static readonly string WaveText = TuiSpinner.WaveText;
    private static readonly string BusyText = WaveText + " esc interrupt";

    private string _left = string.Empty;
    private bool _busy;
    private long _waveBucket;
    private double _waveSeconds;
    private string? _busyHint;
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
    public event Action? WorkspaceChanged;

    public void SetIdle(string workspace)
    {
        Workspace = workspace;
        WorkspaceChanged?.Invoke();
        _left = workspace;
        _busy = false;
        _waveBucket = 0;
        _busyHint = null;
        SetNeedsDraw();
    }

    /// <summary>
    /// Swaps the left text for the colour-wave busy indicator. An optional hint replaces
    /// the default "esc interrupt" copy, e.g. while queued lines wait behind the active
    /// turn.
    /// </summary>
    public void SetBusy(string? hint = null)
    {
        _busy = true;
        _busyHint = hint;
        SetNeedsDraw();
    }

    /// <summary>
    /// Advances the busy wave from the shared animation clock; the phase is a pure
    /// function of elapsed seconds. A no-op while the row is idle or while the phase
    /// bucket has not moved, so an 18 ms ticker repaints about eleven times a second.
    /// </summary>
    public void SetWaveClock(double elapsedSeconds)
    {
        if (!_busy)
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
        // Classic truncates the workspace first so the key hints always survive; the
        // remaining columns are split between the two sides.
        var leftBudget = Math.Max(0, width - rightWidth - 2);
        var leftText = !_busy
            ? _left
            : _busyHint is null
                ? BusyText
                : $"{WaveText} {_busyHint}";
        if (_scrolledBack) leftText = "↑ scrolled back · " + leftText;
        var shownLeft = TuiText.Truncate(leftText, leftBudget);
        var leftWidth = TuiText.VisibleWidth(shownLeft);
        var gap = Math.Max(1, width - leftWidth - rightWidth);

        DrawLeft(leftWidth, shownLeft);
        DrawRight(leftWidth + gap, Math.Max(0, width - leftWidth - gap));
        return true;
    }

    /// <summary>Idle workspace is meta; busy paints the colour wave and <c>esc</c> bright.</summary>
    private void DrawLeft(int maxWidth, string shownText)
    {
        if (_scrolledBack)
        {
            Draw(0, [(shownText, TuiToken.Meta)], maxWidth);
            return;
        }

        if (!_busy)
        {
            Draw(0, [(TuiText.Truncate(_left, maxWidth), TuiToken.Meta)], maxWidth);
            return;
        }

        // The wave dots carry their own per-cell ramp colours; the copy after them keeps
        // the bright "esc" and meta " interrupt" split.
        var column = DrawWave(0, maxWidth);
        var rest = shownText.Length > WaveText.Length ? shownText[WaveText.Length..] : string.Empty;
        if (rest.Length == 0)
        {
            return;
        }

        IReadOnlyList<(string Text, TuiToken Token)> segments = _busyHint is null
            ? rest.Length > 4
                ? [(rest[..4], TuiToken.Bright), (rest[4..], TuiToken.Meta)]
                : [(rest, TuiToken.Bright)]
            : [(rest, TuiToken.Meta)];
        Draw(column, segments, maxWidth - column);
    }

    /// <summary>Draws the wave dots with their per-cell ramp colours; returns the column after them.</summary>
    private int DrawWave(int column, int maxWidth)
    {
        for (var cell = 0; cell < TuiSpinner.WaveCells; cell++)
        {
            if (maxWidth <= 0 || column >= Viewport.Width)
            {
                break;
            }

            SetAttribute(TgPalette.Attr(TuiSpinner.WaveRgbAt(cell, _waveSeconds)));
            Move(column, 0);
            AddStr(TuiSpinner.WaveGlyph);
            column += TuiText.VisibleWidth(TuiSpinner.WaveGlyph);
            maxWidth -= TuiText.VisibleWidth(TuiSpinner.WaveGlyph);
        }

        return column;
    }

    /// <summary>Bright key caps with meta words, matching ComposerBox.ComposeHints.</summary>
    private void DrawRight(int column, int maxWidth)
    {
        if (Compact)
        {
            Draw(column, [("ctrl+p", TuiToken.Bright), (" commands", TuiToken.Meta)], maxWidth);
            return;
        }
        var baseHints = _hasUnseenTranscript
            ? new List<(string Text, TuiToken Token)>
            {
                ("↓ new output", TuiToken.Brand),
                ("  ctrl+end", TuiToken.Bright),
                (" tail  ", TuiToken.Meta),
            }
            : new List<(string Text, TuiToken Token)>();
        baseHints.AddRange(ComposerBox.HintKeySegments.Select(segment =>
            (segment.Text, segment.Token ?? TuiToken.Meta)));

        var width = Viewport.Width;
        if (ShouldShowExtendedHints(width))
        {
            baseHints.AddRange(
            [
                ("  ctrl+p", TuiToken.Bright),
                (" palette", TuiToken.Meta),
                ("  pgup/dn", TuiToken.Bright),
                (" scroll", TuiToken.Meta),
            ]);
            if (!_hasUnseenTranscript)
            {
                baseHints.Add(("  ctrl+end", TuiToken.Bright));
                baseHints.Add((" tail", TuiToken.Meta));
            }
        }

        IReadOnlyList<(string Text, TuiToken Token)> segments = baseHints;
        Draw(column, segments, maxWidth);
    }

    private string GetRightHints(int width)
    {
        if (Compact) return "ctrl+p commands";
        if (_hasUnseenTranscript)
        {
            return ShouldShowExtendedHints(width)
                ? ExtendedDetachedKeysRight
                : DetachedKeysRight;
        }

        return ShouldShowExtendedHints(width)
            ? ExtendedKeysRight
            : KeysRight;
    }

    private bool ShouldShowExtendedHints(int width) =>
        width >= Math.Max(
            ExtendedHintsMinimumWidth,
            TuiText.VisibleWidth(_hasUnseenTranscript ? ExtendedDetachedKeysRight : ExtendedKeysRight) + 12);

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
