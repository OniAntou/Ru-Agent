using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgCommandPaletteTests
{
    [Fact]
    public void FilteringAndSelection_UseTheSharedKnownCommandCatalog()
    {
        var palette = new TgCommandPalette(ChatCommandCatalog.KnownCommands);

        palette.SetQuery("/sta");

        Assert.Equal(["/status"], palette.Matches.Select(command => command.Command));
        palette.Move(1);
        Assert.Equal("/status", palette.SelectedCommand);
    }

    [Fact]
    public void OpeningAndDismissingPalette_RestoresComposerDraft()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        window.Composer.Input.Text = "keep this unfinished draft";
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(250), () =>
        {
            app.InjectKey(Key.P.WithCtrl);
            app.InjectKey(new Key('/'));
            app.InjectKey(new Key('s'));
            app.InjectKey(Key.Esc);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(450), () =>
        {
            Assert.Equal("keep this unfinished draft", window.Composer.Input.Text?.ToString());
            Assert.False(window.IsCommandPaletteOpen);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void AcceptingCommand_PopulatesComposerWithoutSubmittingArguments()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        _ = new TgChatLoop(window);
        window.Composer.Input.Text = "draft";
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(250), () =>
        {
            app.InjectKey(Key.P.WithCtrl);
            app.InjectKey(new Key('/'));
            app.InjectKey(new Key('s'));
            app.InjectKey(new Key('t'));
            app.InjectKey(new Key('a'));
            app.InjectKey(Key.CursorDown);
            app.InjectKey(Key.Enter);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(450), () =>
        {
            Assert.Equal("/status ", window.Composer.Input.Text?.ToString());
            Assert.False(window.IsCommandPaletteOpen);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void BusyTurn_RefusesPaletteSelectionAndLeavesThePaletteOpen()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var promptCalls = 0;
        loop.SetPromptHandler(async (_, _, cancellationToken) =>
        {
            promptCalls++;
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
        });

        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            window.FocusComposer();
            window.Composer.Input.Text = "active request";
            loop.Submit();
            app.InjectKey(Key.P.WithCtrl);
            app.InjectKey(new Key('/'));
            app.InjectKey(new Key('s'));
            app.InjectKey(new Key('t'));
            app.InjectKey(new Key('a'));
            app.InjectKey(Key.Enter);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            Assert.Equal(1, promptCalls);
            Assert.True(window.IsCommandPaletteOpen);
            Assert.Equal(string.Empty, window.Composer.Input.Text?.ToString());
            Assert.Contains("active turn in progress", string.Join('\n', TgSnapshot.Capture(app)));
            app.InjectKey(Key.Esc);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            app.InjectKey(Key.Esc);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(700), () =>
        {
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
