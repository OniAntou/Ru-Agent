using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Testing;
using Terminal.Gui.ViewBase;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgRunLoopTests
{
    [Fact]
    public async Task Submit_EchoesUserBlock_StreamsProse_AndAppendsToolRows()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);

        loop.SetPromptHandler(async (line, stream, ct) =>
        {
            stream.Append(" Em kiểm tra lock", TranscriptStyle.Prose);
            stream.AppendChunk(" trong harness.");
            stream.Append(" ✱ Search \"busy lock\" (18 matches)", TranscriptStyle.Tool);
            await Task.CompletedTask;
        });

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            window.FocusComposer();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "sửa lỗi";
            loop.Submit();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            var text = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Contains("sửa lỗi", text);
            Assert.Contains("Em kiểm tra lock trong harness.", text);
            Assert.Contains("Search", text);
            loop.Stop();
            app.RequestStop();
            return false;
        });
        app.Run(window);
        await loop.Completion;
    }

    /// <summary>
    /// Task 8's parked finding: focus granted to the composer after the run starts must
    /// win against a view that already owns focus. The real wiring calls
    /// <see cref="TgChatWindow.FocusComposer"/> from a timeout once the window is live.
    /// </summary>
    [Fact]
    public void FocusComposer_TakesFocusFromACompetingView()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var competitor = new View { X = 0, Y = 0, Width = 1, Height = 1, CanFocus = true };
        window.Add(competitor);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            competitor.SetFocus();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(350), () =>
        {
            Assert.True(competitor.HasFocus);
            window.FocusComposer();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(450), () =>
        {
            Assert.True(window.Composer.Input.HasFocus);
            Assert.True(window.Composer.Input.Cursor?.IsVisible);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
