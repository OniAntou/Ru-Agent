using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgCommandOutputTests
{
    [Fact]
    public void Nested_commands_share_their_result_but_independent_output_is_not_captured()
    {
        using var transcript = new StringWriter();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? command = null;
        Run((app, window, stage) =>
        {
            if (stage == 0)
            {
                var output = new TgCommandOutput(window, transcript);
                command = output.RunAsync("outer", false, async () =>
                {
                    output.Output.WriteLine("outer result");
                    await output.RunAsync("inner", false, () =>
                    {
                        output.Output.WriteLine("inner result");
                        return Task.CompletedTask;
                    }, default);
                    entered.SetResult();
                    await release.Task;
                }, default);
                // This task is created outside the command's execution context.
                _ = Task.Run(async () =>
                {
                    await entered.Task;
                    output.Output.WriteLine("independent run");
                    release.SetResult();
                });
                return true;
            }
            if (stage == 1)
            {
                Assert.True(window.IsDocumentPanelOpen);
                var screen = string.Join('\n', TgSnapshot.Capture(app));
                Assert.Contains("outer result", screen);
                Assert.Contains("inner result", screen);
                Assert.DoesNotContain("independent run", screen);
                Assert.Equal("independent run\n", transcript.ToString().Replace("\r\n", "\n"));
                app.InjectKey(Key.Esc);
                return true;
            }
            Assert.True(command!.IsCompletedSuccessfully);
            Assert.True(window.Landing.Visible);
            Assert.Same(window.Composer.Input, window.MostFocused);
            return false;
        });
    }

    [Fact]
    public void Cancelled_command_discards_partial_result_and_releases_its_capture()
    {
        using var transcript = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? command = null;
        TgCommandOutput? output = null;
        Run((_, window, stage) =>
        {
            if (stage == 0)
            {
                output = new TgCommandOutput(window, transcript);
                command = output.RunAsync("pending", false, async () =>
                {
                    output.Error.WriteLine("partial result");
                    entered.SetResult();
                    await Task.Delay(System.Threading.Timeout.Infinite, cancellation.Token);
                }, cancellation.Token);
                return true;
            }
            if (stage == 1)
            {
                Assert.True(entered.Task.IsCompleted);
                cancellation.Cancel();
                return true;
            }
            Assert.True(command!.IsCanceled);
            output!.Output.WriteLine("later result");
            Assert.DoesNotContain("partial result", transcript.ToString());
            Assert.Contains("later result", transcript.ToString());
            Assert.False(window.IsDocumentPanelOpen);
            Assert.True(window.Landing.Visible);
            return false;
        });
    }

    [Fact]
    public void Oversized_command_result_has_a_visible_truncation_notice()
    {
        using var transcript = new StringWriter();
        Task? command = null;
        Run((app, window, stage) =>
        {
            if (stage == 0)
            {
                var output = new TgCommandOutput(window, transcript);
                command = output.RunAsync("large result", false, () =>
                {
                    output.Output.WriteLine(new string('x', 1_000_000));
                    return Task.CompletedTask;
                }, default);
                return true;
            }
            if (stage == 1)
            {
                Assert.True(window.IsDocumentPanelOpen);
                window.Document.ScrollTo(int.MaxValue);
                return true;
            }
            if (stage == 2)
            {
                Assert.Contains("Command output truncated.", string.Join('\n', TgSnapshot.Capture(app)));
                app.InjectKey(Key.Esc);
                return true;
            }
            Assert.True(command!.IsCompletedSuccessfully);
            Assert.Empty(transcript.ToString());
            Assert.True(window.Landing.Visible);
            return false;
        });
    }

    private static void Run(Func<IApplication, TgChatWindow, int, bool> inspect)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        Exception? failure = null;
        var stage = 0;
        var complete = false;
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            try
            {
                if (inspect(app, window, stage++)) return true;
                complete = true;
            }
            catch (Exception error) { failure = error; }
            app.RequestStop();
            return false;
        });
        app.AddTimeout(TimeSpan.FromSeconds(8), () => { app.RequestStop(); return false; });
        app.Run(window);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(complete);
    }
}
