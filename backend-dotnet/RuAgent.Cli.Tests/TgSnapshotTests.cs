using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgSnapshotTests
{
    [Fact]
    public void HeadlessApp_RendersALabel()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        window.Add(new Label { Text = "ru smoke", X = 0, Y = 0 });
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rows = RuAgent.Cli.TerminalGui.TgSnapshot.Capture(app);
            Assert.Contains("ru smoke", rows[0]);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
