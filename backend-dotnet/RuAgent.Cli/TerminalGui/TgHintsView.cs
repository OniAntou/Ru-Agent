using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Hint row: idle workspace left, key hints right; busy swaps in the spinner.</summary>
public sealed class TgHintsView : View
{
    private const string KeysRight = "/ commands  tab complete  ctrl+o expand";
    private string _left = string.Empty;
    private string? _spinnerFrame;
    private string? _busyHint;

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

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        var rightWidth = TuiText.VisibleWidth(KeysRight);
        // Classic truncates the workspace first so the key hints always survive; the
        // remaining columns are split between the two sides.
        var leftBudget = Math.Max(0, width - rightWidth - 2);
        var leftText = _spinnerFrame is null
            ? _left
            : _busyHint is null
                ? $"{_spinnerFrame} esc interrupt"
                : $"{_spinnerFrame} {_busyHint}";
        var shownLeft = TuiText.Truncate(leftText, leftBudget);
        var leftWidth = TuiText.VisibleWidth(shownLeft);
        var gap = Math.Max(1, width - leftWidth - rightWidth);

        DrawLeft(leftWidth);
        DrawRight(leftWidth + gap, Math.Max(0, width - leftWidth - gap));
        return true;
    }

    /// <summary>Idle workspace is meta; busy paints the spinner and <c>esc</c> bright.</summary>
    private void DrawLeft(int maxWidth)
    {
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
        IReadOnlyList<(string Text, TuiToken Token)> segments =
        [
            ("/", TuiToken.Bright),
            (" commands", TuiToken.Meta),
            ("  ", TuiToken.Meta),
            ("tab", TuiToken.Bright),
            (" complete", TuiToken.Meta),
            ("  ", TuiToken.Meta),
            ("ctrl+o", TuiToken.Bright),
            (" expand", TuiToken.Meta),
        ];
        Draw(column, segments, maxWidth);
    }

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
