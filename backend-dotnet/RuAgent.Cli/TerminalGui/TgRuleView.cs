using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>A quiet boundary between session context and conversation.</summary>
public sealed class TgRuleView : View
{
    public TgRuleView() { Height = 1; CanFocus = false; }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (Viewport.Width <= 0) return true;
        SetAttribute(TgPalette.Attr(TuiToken.Line));
        Move(0, 0);
        AddStr(new string('─', Viewport.Width));
        return true;
    }
}
