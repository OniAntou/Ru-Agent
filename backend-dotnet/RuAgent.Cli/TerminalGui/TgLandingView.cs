using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// The pre-chat home surface: the "ru agent" block wordmark ("ru" aurora-green,
/// "agent" light gray, diagonal light falloff) centered in the
/// transcript region. Hidden as soon as the transcript has content and restored when
/// <c>/clear</c> empties it, mirroring the classic landing screen. A meteor clump falls across
/// the sky while visible, advanced by <see cref="Tick"/> on the UI loop.
/// </summary>
public sealed class TgLandingView : View
{
    private int _meteorTick;
    private bool _meteorTicked;
    private bool _meteorWasVisible;

    public TgLandingView()
    {
        X = 0;
        Y = 1;
        Width = Dim.Fill();
        Height = Dim.Fill(8);
        SetScheme(TgPalette.Scheme(TuiToken.Meta));
    }

    /// <summary>
    /// Advances one meteor frame. Redraws only while the meteor is on screen, so an idle
    /// sky costs nothing; before the first tick nothing moves and snapshots stay still.
    /// </summary>
    public void Tick()
    {
        if (!Visible)
        {
            _meteorWasVisible = false;
            return;
        }

        _meteorTick++;
        _meteorTicked = true;
        var cells = TuiHeader.MeteorCells(_meteorTick, Viewport.Width, Viewport.Height);
        var visible = cells.Count > 0;
        if (visible || _meteorWasVisible)
        {
            SetNeedsDraw();
        }

        _meteorWasVisible = visible;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        var height = Viewport.Height;
        if (width <= 0 || height <= 0)
        {
            return true;
        }

        if (width < TuiHeader.MinWordmarkWidth)
        {
            const string fallback = "Ru Agent";
            SetAttribute(TgPalette.Attr(TuiToken.Aurora));
            Move(Math.Max(0, (width - fallback.Length) / 2), Math.Max(0, height / 2));
            AddStr(fallback);
            return true;
        }

        var ru = TuiHeader.WordmarkRuRows;
        var agent = TuiHeader.WordmarkAgentRows;
        var blockWidth = 0;
        for (var i = 0; i < ru.Count; i++)
        {
            blockWidth = Math.Max(blockWidth, TuiText.VisibleWidth(ru[i]) + 1 + TuiText.VisibleWidth(agent[i]));
        }

        var left = Math.Max(0, (width - blockWidth) / 2);
        var top = Math.Max(0, (height - ru.Count) / 2);
        TuiToken? current = null;
        for (var r = 0; r < height; r++)
        {
            Move(0, r);
            for (var c = 0; c < width; c++)
            {
                var star = TuiHeader.StarCell(c, r);
                var token = star?.Token;
                if (token != current)
                {
                    if (token is { } paint)
                    {
                        SetAttribute(TgPalette.Attr(paint));
                    }

                    current = token;
                }

                AddStr(star?.Glyph.ToString() ?? " ");
            }
        }

        if (_meteorTicked)
        {
            foreach (var (col, row, glyph, token) in TuiHeader.MeteorCells(_meteorTick, width, height))
            {
                Move(col, row);
                SetAttribute(TgPalette.Attr(token));
                AddStr(glyph.ToString());
            }
        }

        current = null;
        for (var i = 0; i < ru.Count; i++)
        {
            Move(left, top + i);
            foreach (var (glyph, token) in TuiHeader.WordmarkRowCells(i))
            {
                if (token != current)
                {
                    if (token is { } paint)
                    {
                        SetAttribute(TgPalette.Attr(paint));
                    }

                    current = token;
                }

                AddStr(glyph.ToString());
            }
        }

        return true;
    }
}
