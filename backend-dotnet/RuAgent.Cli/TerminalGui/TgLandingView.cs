using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// The pre-chat home surface: the two-tone "ru agent" block wordmark centered in the
/// transcript region. Hidden as soon as the transcript has content and restored when
/// <c>/clear</c> empties it, mirroring the classic landing screen.
/// </summary>
public sealed class TgLandingView : View
{
    public TgLandingView()
    {
        X = 0;
        Y = 1;
        Width = Dim.Fill();
        Height = Dim.Fill(8);
        SetScheme(TgPalette.Scheme(TuiToken.Meta));
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
            SetAttribute(TgPalette.Attr(TuiToken.Bright));
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
        for (var i = 0; i < ru.Count; i++)
        {
            Move(left, top + i);
            SetAttribute(TgPalette.Attr(TuiToken.Meta));
            AddStr(ru[i]);
            AddStr(" ");
            SetAttribute(TgPalette.Attr(TuiToken.Bright));
            AddStr(agent[i]);
        }

        return true;
    }
}
