using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgPanelsTests
{
    [Fact]
    public void SlashDropdown_ListsCommandsAboveTheComposer()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Composer.Input.Text = "/";
        window.ShowSlashDropdown();
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rows = TgSnapshot.Capture(app);
            Assert.Contains("/model", string.Join('\n', rows));
            Assert.Contains("/attach", string.Join('\n', rows));
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void ApprovalPanel_RendersChoicesAndEsc()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.ShowApproval("terminal_delete_requires_approval");
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var text = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Contains("1 · once", text);
            Assert.Contains("2 · deny", text);
            Assert.Contains("3 · always", text);
            Assert.Contains("esc", text);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
