using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Responsive composition in terminal cells; interactive state stays in the existing owners.</summary>
internal static class TgChatLayout
{
    public static void Apply(TgChatWindow window, int columns, int rows)
    {
        var home = window.Landing.Visible;
        window.Header.Visible = !home;
        window.Header.X = 2;
        window.Header.Width = Dim.Fill(2);
        window.Header.Height = rows >= 20 ? 2 : 1;
        window.HeaderDivider.Visible = !home;
        window.HeaderDivider.X = 2;
        window.HeaderDivider.Width = Dim.Fill(2);
        window.HeaderDivider.Y = Pos.Bottom(window.Header);
        window.Transcript.X = 2;
        window.Transcript.Y = Pos.Bottom(window.Header) + 1;
        window.Transcript.Width = Dim.Fill(2);
        var width = home ? Math.Min(80, Math.Max(10, columns - 4)) : Math.Max(10, columns - 4);
        var homeLeft = Math.Max(0, (columns - width) / 2);
        window.Composer.SetViewportBudget(width, rows - 10);
        window.Composer.Height = window.Composer.LayoutHeight;
        var homeTop = Math.Max(0, (rows - (TuiHeader.WordmarkRows + window.Composer.LayoutHeight + 3)) / 2);
        window.Composer.X = homeLeft;
        window.Composer.Width = width;
        window.Composer.Y = home ? homeTop + TuiHeader.WordmarkRows + 2 : Pos.AnchorEnd(window.Composer.LayoutHeight + 1);
        window.Transcript.Height = Dim.Fill(window.Composer.LayoutHeight + 2);
        window.Hints.X = Pos.Left(window.Composer);
        window.Hints.Compact = home;
        window.Hints.Width = Dim.Width(window.Composer);
        window.Hints.Y = home ? Pos.Bottom(window.Composer) : Pos.AnchorEnd(1);
        window.Landing.WordmarkRow = homeTop;
    }
}
