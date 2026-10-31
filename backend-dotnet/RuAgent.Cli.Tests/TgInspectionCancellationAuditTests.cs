using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgInspectionCancellationAuditTests
{
    [Theory]
    [InlineData("/tasks", false)]
    [InlineData("/changes", false)]
    [InlineData("/history", false)]
    [InlineData("/tasks", true)]
    public async Task Escape_cancels_pending_inspection_before_its_panel_opens_without_cancelling_the_model(string command, bool runningModel)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        CancellationToken inspectionToken = default;
        var inspectionEntered = false;
        CancellationToken modelToken = default;
        loop.SetPromptHandler(async (_, _, ct) =>
        {
            modelToken = ct;
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
        });
        async Task Inspect(string? argument, CancellationToken ct)
        {
            inspectionEntered = true;
            inspectionToken = ct;
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
        }
        loop.SetTaskCommandHandler(Inspect);
        loop.SetChangesCommandHandler(Inspect);
        loop.SetRewindCommandHandler(Inspect);
        var stage = 0;
        var observedEscapes = 0;
        app.Keyboard.KeyDown += (_, key) => { if (key == Key.Esc) observedEscapes++; };
        Exception? failure = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            try
            {
                window.FocusComposer();
                if (stage == 0)
                {
                    if (runningModel) { window.Composer.Input.Text = "work"; loop.Submit(); }
                    window.Composer.Input.Text = command;
                    loop.Submit();
                    stage++;
                    return true;
                }
                if (stage == 1)
                {
                    Assert.Equal(runningModel, loop.IsBusy);
                    Assert.True(inspectionEntered);
                    Assert.False(window.IsSlashDropdownOpen);
                    Assert.True(window.Composer.Input.HasFocus);
                    app.InjectKey(Key.Esc);
                    stage++;
                    return true;
                }
                if (observedEscapes == 0) return true;
                Assert.False(window.IsModalPanelOpen);
                Assert.True(inspectionToken.IsCancellationRequested);
                Assert.False(modelToken.IsCancellationRequested);
                stage++;
            }
            catch (Exception error) { failure = error; }
            loop.Stop();
            app.RequestStop();
            return false;
        });
        app.AddTimeout(TimeSpan.FromSeconds(5), () => { loop.Stop(); app.RequestStop(); return false; });
        app.Run(window);
        await loop.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(3, stage);
    }
}
