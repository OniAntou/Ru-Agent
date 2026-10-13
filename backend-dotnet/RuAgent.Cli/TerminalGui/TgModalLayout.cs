using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Modal cards centre on the terminal, independently of composer placement.</summary>
internal static class TgModalLayout
{
    public static void Apply(View panel, View window, int maxRows = int.MaxValue)
    {
        panel.Width = Dim.Func(_ => Math.Max(1, Math.Min(TuiCard.MaxWidth, window.Viewport.Width - 4)));
        panel.Height = Dim.Func(_ => Math.Max(1, Math.Min(maxRows, window.Viewport.Height - 2)));
        panel.X = Pos.Center();
        panel.Y = Pos.Center();
    }
}
