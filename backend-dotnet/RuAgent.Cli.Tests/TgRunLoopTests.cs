using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
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

    [Fact]
    public void HistoryUpDown_ReplacesTheBuffer()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        loop.SetPromptHandler((_, _, _) => Task.CompletedTask);
        loop.RememberForTests("lần trước");

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            app.InjectKey(Key.CursorUp);
            Assert.Equal("lần trước", window.Composer.Input.Text?.ToString());
            app.InjectKey(Key.CursorDown);
            Assert.Equal(string.Empty, window.Composer.Input.Text?.ToString());
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public async Task SubmitWhileBusy_QueuesInsteadOfInterleaving()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var gate = new TaskCompletionSource();
        var seen = new List<string>();
        loop.SetPromptHandler(async (line, _, _) => { seen.Add(line); await gate.Task; });

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "một";
            loop.Submit();
            window.Composer.Input.Text = "hai";
            loop.Submit();
            Assert.Equal(["một"], seen);
            gate.SetResult();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            Assert.Equal(["một", "hai"], seen);
            app.RequestStop();
            return false;
        });
        app.Run(window);
        await loop.Completion;
    }

    [Fact]
    public void Enter_SubmitsExactlyOnce_AndClearsComposer()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var submitted = new List<string>();
        loop.SetPromptHandler((line, _, _) =>
        {
            submitted.Add(line);
            return Task.CompletedTask;
        });

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "xin chào";
            app.InjectKey(Key.Enter);
            Assert.Equal(["xin chào"], submitted);
            Assert.Equal(string.Empty, window.Composer.Input.Text?.ToString());
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void Esc_CancelsInFlightTurn()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var cancelled = false;
        loop.SetPromptHandler(async (_, _, ct) =>
        {
            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }
        });

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "chạy lâu";
            app.InjectKey(Key.Enter);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(450), () =>
        {
            app.InjectKey(Key.Esc);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(700), () =>
        {
            Assert.True(cancelled);
            var text = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Contains("esc interrupted", text);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void Tab_CompletesKnownCommand()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        loop.SetPromptHandler((_, _, _) => Task.CompletedTask);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "/mod";
            app.InjectKey(Key.Tab);
            Assert.Equal("/model ", window.Composer.Input.Text?.ToString());
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void CtrlO_TogglesTheLastCollapsedBlock()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        loop.SetPromptHandler((_, _, _) => Task.CompletedTask);
        window.Transcript.AppendCollapsed(
            "    │ · 2 more lines · ctrl+o",
            ["    │ + added line", "    │ - removed line"],
            TranscriptStyle.Tool);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            app.InjectKey(Key.O.WithCtrl);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            var expanded = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Contains("+ added line", expanded);
            Assert.Contains("- removed line", expanded);
            app.InjectKey(Key.O.WithCtrl);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(700), () =>
        {
            var collapsed = string.Join('\n', TgSnapshot.Capture(app));
            Assert.DoesNotContain("+ added line", collapsed);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
