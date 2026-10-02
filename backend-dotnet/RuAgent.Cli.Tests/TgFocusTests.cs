using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgFocusTests
{
    [Fact]
    public void Composer_ReceivesFocusAndCaret()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            window.SetFocus();
            window.Composer.Input.SetFocus();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            Assert.True(window.Composer.Input.HasFocus);
            Assert.True(window.Composer.Input.Cursor?.IsVisible);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void TypedKeys_LandInTheComposerBuffer()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            window.SetFocus();
            window.Composer.Input.SetFocus();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            app.InjectKey(Key.A);
            app.InjectKey(Key.B);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            Assert.Equal("ab", window.Composer.Input.Text?.ToString());
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
