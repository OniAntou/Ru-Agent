using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgLandingTests
{
    [Fact]
    public void Landing_ShowsWordmark_UntilContentArrives_AndReturnsOnClear()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var captures = new List<string>();

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            captures.Add(string.Join('\n', TgSnapshot.Capture(app)));
            window.Transcript.Append(" ▌ xin chào", TranscriptStyle.User);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            captures.Add(string.Join('\n', TgSnapshot.Capture(app)));
            window.Transcript.Clear();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(600), () =>
        {
            captures.Add(string.Join('\n', TgSnapshot.Capture(app)));
            app.RequestStop();
            return false;
        });
        app.Run(window);

        Assert.Contains("█▀▀█ █  █ █▀▀█", captures[0]);

        // The feet row is gone, and U now closes its own bowl so the letter still reads as a U
        // rather than as two bare stems.
        Assert.DoesNotContain("▀  ▀ ▀▀▀▀", captures[0]);
        Assert.Contains("█  █ █▄▄█", captures[0]);
        Assert.Contains(captures[0], ch => ch is >= '\u2801' and <= '\u28ff');

        Assert.DoesNotContain("█▀▀█ █  █ █▀▀█", captures[1]);
        Assert.Contains("xin chào", captures[1]);
        Assert.DoesNotContain(captures[1], ch => ch is >= '\u2801' and <= '\u28ff');
        Assert.Contains("█▀▀█ █  █ █▀▀█", captures[2]);
        Assert.Contains(captures[2], ch => ch is >= '\u2801' and <= '\u28ff');
    }
}
