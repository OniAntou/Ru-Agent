using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// Picking a command from the <c>/</c> list must not cost a second Enter: a command that is
/// complete on its own runs straight away (so its card opens), and only a command that still
/// needs an argument is left in the composer for the rest to be typed.
/// </summary>
public sealed class TgSlashSubmitTests
{
    private static (string Composer, List<string> Ran) Pick(IApplication app, TgChatWindow window, string typed)
    {
        var ran = new List<string>();
        var loop = new TgChatLoop(window);
        loop.SetPromptHandler((line, _, _) =>
        {
            ran.Add(line.Trim());
            return Task.CompletedTask;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            window.FocusComposer();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            foreach (var ch in typed)
            {
                app.InjectKey(new Key(ch));
            }

            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            app.InjectKey(Key.Enter);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(700), () =>
        {
            app.RequestStop();
            return false;
        });
        app.Run(window);
        return (window.Composer.Input.Text?.ToString() ?? string.Empty, ran);
    }

    [Theory]
    [InlineData("/", "/model")]
    [InlineData("/model", "/model")]
    [InlineData("/reasoning", "/reasoning")]
    [InlineData("/clear", "/clear")]
    [InlineData("/stats", "/stats")]
    public void ACompleteCommand_RunsTheMomentItIsPicked(string typed, string expected)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();

        var (composer, ran) = Pick(app, window, typed);

        Assert.Equal([expected], ran);
        // Running it consumed the composer, so the command is not left behind as text.
        Assert.Equal(string.Empty, composer);
    }

    [Theory]
    [InlineData("/rewind", "/rewind")]
    [InlineData("/attach", "/attach")]
    [InlineData("/detach", "/detach")]
    public void ACommandNeedingAnArgument_StaysInTheComposer(string typed, string expected)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();

        var (composer, ran) = Pick(app, window, typed);

        Assert.Empty(ran);
        Assert.Equal(expected + " ", composer);
    }

    [Fact]
    public void TabOnlyFillsTheCommandAndNeverRunsIt()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var ran = new List<string>();
        var loop = new TgChatLoop(window);
        loop.SetPromptHandler((line, _, _) =>
        {
            ran.Add(line.Trim());
            return Task.CompletedTask;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
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
            app.InjectKey(Key.Tab);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(700), () =>
        {
            app.RequestStop();
            return false;
        });
        app.Run(window);

        // Tab is "apply the selection", not "run it": the operator can still add an argument.
        Assert.Empty(ran);
        Assert.Equal("/model ", window.Composer.Input.Text?.ToString());
    }

    [Fact]
    public void NeedsArgument_OnlyForCommandsThatCannotRunBare()
    {
        foreach (var command in new[] { "/attach", "/detach", "/rewind" })
        {
            Assert.True(ChatCommandCatalog.NeedsArgument(command), command);
        }

        foreach (var command in new[] { "/model", "/backend", "/key", "/reasoning", "/delete", "/clear", "/exit", "/help", "/tree" })
        {
            Assert.False(ChatCommandCatalog.NeedsArgument(command), command);
        }

        Assert.False(ChatCommandCatalog.NeedsArgument(null));
    }
}