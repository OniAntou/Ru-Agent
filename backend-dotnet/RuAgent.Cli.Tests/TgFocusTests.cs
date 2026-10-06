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
            window.FocusComposer();
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
            window.FocusComposer();
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

    [Fact]
    public void SlashDropdown_KeepsTheComposerFocusedSoTypingStillFilters()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            window.FocusComposer();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            app.InjectKey(new Key('/'));
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            Assert.True(window.IsSlashDropdownOpen);
            // Opening the list must not take focus: a focusable dropdown was added to the
            // window and took it, which left every following character with a view that
            // ignores plain keys, so typing silently did nothing.
            Assert.True(window.Composer.Input.HasFocus);
            foreach (var character in "model")
            {
                app.InjectKey(new Key(character));
            }

            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(600), () =>
        {
            Assert.Equal("/model", window.Composer.Input.Text?.ToString());
            Assert.True(window.IsSlashDropdownOpen);

            // And the list followed the composer instead of staying on all twenty commands.
            var text = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Contains("/model", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/key", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/backend", text, StringComparison.Ordinal);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
