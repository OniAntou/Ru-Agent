using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgWelcomeChromeTests
{
    [Fact]
    public void Welcome_hides_permission_and_ready_chat_restores_permission_and_reset_hides_it_again()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Composer.SetMeta("standard", "model", "provider");
        window.Hints.SetIdle("C:/workspace"); var step = 0; Exception? failure = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            try
            {
                var screen = TgSnapshot.Capture(app);
                if (step == 0)
                {
                    Assert.True(window.Header.Visible); Assert.True(window.Landing.Visible);
                    Assert.DoesNotContain("standard", string.Join('\n', screen));
                    Assert.DoesNotContain("Ready", string.Join('\n', screen));
                    window.Composer.SetMeta("plan", "model", "provider"); step++; return true;
                }
                if (step == 1)
                {
                    Assert.True(window.Header.Visible); Assert.Equal("plan", window.Header.Preset);
                    Assert.DoesNotContain("plan", string.Join('\n', screen));
                    window.Transcript.Append("hello", TranscriptStyle.Prose); step++; return true;
                }
                if (step == 2)
                {
                    Assert.True(window.Header.Visible); Assert.Contains("plan", screen[window.Header.Frame.Y]);
                    Assert.DoesNotContain("Ready", string.Join('\n', screen));
                    window.ResetTranscript(); step++; return true;
                }
                Assert.True(window.Header.Visible); Assert.True(window.Landing.Visible);
                Assert.DoesNotContain("plan", string.Join('\n', screen));
                step++; app.RequestStop(); return false;
            }
            catch (Exception ex) { failure = ex; app.RequestStop(); return false; }
        });
        app.AddTimeout(TimeSpan.FromSeconds(5), () => { app.RequestStop(); return false; });
        app.Run(window);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(4, step);
    }
}
