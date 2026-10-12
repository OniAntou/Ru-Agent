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
    private string _left = string.Empty;
    private string? _spinnerFrame;
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

    public void SetIdle(string workspace)
    {
        Workspace = workspace;
        _left = workspace;
        _spinnerFrame = null;
        _busyHint = null;
        SetNeedsDraw();
    }

    /// <summary>
    /// Swaps the left text for the spinner. An optional hint replaces the default
    /// "esc interrupt" copy, e.g. while queued lines wait behind the active turn.
    /// </summary>
    public void SetBusy(string spinnerFrame, string? hint = null)
    {
        _spinnerFrame = spinnerFrame;
        _busyHint = hint;
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
        var leftText = _spinnerFrame is null
            ? _left
            : _busyHint is null
                ? $"{_spinnerFrame} esc interrupt"
                : $"{_spinnerFrame} {_busyHint}";
        if (_scrolledBack) leftText = "↑ scrolled back · " + leftText;
        var shownLeft = TuiText.Truncate(leftText, leftBudget);
        var leftWidth = TuiText.VisibleWidth(shownLeft);
        var gap = Math.Max(1, width - leftWidth - rightWidth);

        DrawLeft(leftWidth, shownLeft);
        DrawRight(leftWidth + gap, Math.Max(0, width - leftWidth - gap));
        return true;
    }

    /// <summary>Idle workspace is meta; busy paints the spinner and <c>esc</c> bright.</summary>
    private void DrawLeft(int maxWidth, string shownText)
    {
        if (_scrolledBack)
        {
            Draw(0, [(shownText, TuiToken.Meta)], maxWidth);
            return;
        }
        IReadOnlyList<(string Text, TuiToken Token)> segments = _spinnerFrame is null
            ?
            [
                (TuiText.Truncate(_left, maxWidth), TuiToken.Meta),
            ]
            : _busyHint is null
                ?
                [
                    (_spinnerFrame, TuiToken.Bright),
                    (" ", TuiToken.Bright),
                    ("esc", TuiToken.Bright),
                    (" interrupt", TuiToken.Meta),
                ]
                :
                [
                    (_spinnerFrame, TuiToken.Bright),
                    (" ", TuiToken.Bright),
                    (_busyHint, TuiToken.Meta),
                ];
        Draw(0, segments, maxWidth);
    }

    /// <summary>Bright key caps with meta words, matching ComposerBox.ComposeHints.</summary>
    private void DrawRight(int column, int maxWidth)
    {
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
