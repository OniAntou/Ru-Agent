using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgChatWindowTests
{
    [Fact]
    public void Layout_PinsHeaderComposerAndHints()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Header.SetTitle("Fix the busy lock");
        window.Header.UpdateUsage(39413, 20, 0.29m);
        window.Composer.SetMeta("Standard", "stealth/space-bunny-alpha", "models.dev:kilo");
        window.Hints.SetIdle("C:/Users/USER");
        window.Transcript.Append(" ▌ sửa lỗi busy lock", TranscriptStyle.User);
        window.Transcript.Append(" Em kiểm tra lock.", TranscriptStyle.Prose);
        window.Transcript.Append(" ✱ Search \"busy lock\" (18 matches)", TranscriptStyle.Tool);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rows = TgSnapshot.Capture(app);
            Assert.Contains("Fix the busy lock", rows[0]);
            Assert.Contains("sửa lỗi busy lock", string.Join('\n', rows));
            Assert.Contains("Search", string.Join('\n', rows));
            Assert.Contains("┌", rows[^7]);
            Assert.Contains("/ commands", rows[^1]);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void DetachedTranscript_ShowsUnreadOutputHintUntilReturningToTail()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Transcript.ScrollToTop();
        window.Transcript.Append("new background output", TranscriptStyle.Prose);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            Assert.Contains("↓ new output", string.Join('\n', TgSnapshot.Capture(app)));
            window.Transcript.ScrollToTail();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(350), () =>
        {
            Assert.DoesNotContain("↓ new output", string.Join('\n', TgSnapshot.Capture(app)));
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
