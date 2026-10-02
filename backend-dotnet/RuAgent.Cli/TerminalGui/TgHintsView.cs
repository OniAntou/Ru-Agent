using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Hint row: idle workspace left, key hints right; busy swaps in the spinner.</summary>
public sealed class TgHintsView : View
{
    private const string KeysRight = "/ commands  tab complete  ctrl+o expand";
    private string _left = string.Empty;

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
        SetNeedsDraw();
    }

    public void SetBusy(string spinnerFrame) => SetLeft($"{spinnerFrame} esc interrupt");

    private void SetLeft(string left)
    {
        _left = left;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        var right = KeysRight;
        var gap = Math.Max(1, width - _left.Length - right.Length);
        SetAttribute(TgPalette.Attr(TuiToken.Meta));
        Move(0, 0);
        AddStr(TuiText.Truncate(_left + new string(' ', gap) + right, width));
        return true;
    }
}
