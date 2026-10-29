using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Responsive composition in terminal cells; interactive state stays in the existing owners.</summary>
internal static class TgChatLayout
{
    public static void Apply(TgChatWindow window, int columns, int rows)
    {
        var home = window.Landing.Visible;
        window.Header.Visible = true;
        window.Header.ShowSessionDetails = !home;
        window.Header.X = 2;
        window.Header.Width = Dim.Fill(2);
        window.Header.Height = !home && (rows >= 20 || columns < 60 && rows >= 12) ? 2 : 1;
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
        // Keep the galaxy's canvas independent of the prompt: moving input up
        // must not pull the core up again and leave the logo chasing it.
        var maxComposerTop = Math.Max(0, rows - window.Composer.LayoutHeight - 2);
        var maxLogoTop = Math.Max(0, maxComposerTop - TuiHeader.WordmarkRows - 1);
        var skyRows = Math.Clamp(Math.Min((int)(rows * 0.75) - 1,
            (int)((maxLogoTop + TuiHeader.WordmarkRows / 2d) / TuiGalaxySky.CoreYFraction)),
            0, TuiGalaxySky.MaxRows);
        var homeTop = Math.Clamp((int)Math.Round(skyRows * TuiGalaxySky.CoreYFraction
            - TuiHeader.WordmarkRows / 2d), 0, maxLogoTop);
        var homeComposerTop = Math.Min(maxComposerTop, homeTop + TuiHeader.WordmarkRows + 2);
        window.Composer.X = homeLeft;
        window.Composer.Width = width;
        window.Composer.Y = home ? homeComposerTop : Pos.AnchorEnd(window.Composer.LayoutHeight + 1);
        window.Transcript.Height = Dim.Fill(window.Composer.LayoutHeight + 2);
        window.Hints.X = Pos.Left(window.Composer);
        window.Hints.Compact = home;
        window.Hints.Width = Dim.Width(window.Composer);
        window.Hints.Y = home ? Pos.Bottom(window.Composer) : Pos.AnchorEnd(1);
        window.Landing.WordmarkRow = homeTop;
        window.Landing.SkyRows = skyRows;
        window.Landing.HintRow = homeComposerTop + window.Composer.LayoutHeight;
        window.Landing.ComposerBounds = new System.Drawing.Rectangle(homeLeft, homeComposerTop,
            width, window.Composer.LayoutHeight);
    }
}
