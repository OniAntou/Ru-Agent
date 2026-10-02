using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgChromeTests
{
    [Fact]
    public void Header_ShowsTitleAndUsageCluster()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var header = new TgHeaderView { X = 0, Y = 0, Width = Dim.Fill() };
        header.SetTitle("Fix the busy lock");
        header.UpdateUsage(totalTokens: 39413, percent: 20, cost: 0.29m);
        window.Add(header);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var row = TgSnapshot.Capture(app)[0];
            Assert.Contains("Fix the busy lock", row);
            Assert.Contains("39,413", row);
            Assert.Contains("20%", row);
            Assert.Contains("$0.29", row);
            var hash = row.IndexOf('#');
            Assert.Equal(TgPalette.ToColor(TuiToken.Bright), TgSnapshot.CellAttribute(app, 0, hash).Foreground);
            var tally = row.IndexOf("39,413", StringComparison.Ordinal);
            Assert.Equal(TgPalette.ToColor(TuiToken.Meta), TgSnapshot.CellAttribute(app, 0, tally).Foreground);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void Hints_IdleShowsWorkspaceAndKeys_BusyShowsSpinner()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var hints = new TgHintsView { X = 0, Y = 0, Width = Dim.Fill() };
        hints.SetIdle("C:/Users/USER");
        window.Add(hints);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var row = TgSnapshot.Capture(app)[0];
            Assert.Contains("C:/Users/USER", row);
            Assert.Contains("/ commands", row);
            Assert.Contains("ctrl+o", row);
            var words = row.IndexOf("/ commands", StringComparison.Ordinal);
            Assert.Equal(TgPalette.ToColor(TuiToken.Meta), TgSnapshot.CellAttribute(app, 0, words + 2).Foreground);
            var tab = row.IndexOf("tab", StringComparison.Ordinal);
            Assert.Equal(TgPalette.ToColor(TuiToken.Bright), TgSnapshot.CellAttribute(app, 0, tab).Foreground);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void Hints_BusyShowsSpinnerAndInterrupt()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var hints = new TgHintsView { X = 0, Y = 0, Width = Dim.Fill() };
        hints.SetBusy("⠋");
        window.Add(hints);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var row = TgSnapshot.Capture(app)[0];
            Assert.Contains("⠋ esc interrupt", row);
            var spinner = row.IndexOf('⠋');
            Assert.Equal(TgPalette.ToColor(TuiToken.Bright), TgSnapshot.CellAttribute(app, 0, spinner).Foreground);
            var esc = row.IndexOf("esc", StringComparison.Ordinal);
            Assert.Equal(TgPalette.ToColor(TuiToken.Bright), TgSnapshot.CellAttribute(app, 0, esc).Foreground);
            Assert.Equal(TgPalette.ToColor(TuiToken.Meta), TgSnapshot.CellAttribute(app, 0, esc + 4).Foreground);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
