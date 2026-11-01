using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgSessionHostWindowTests
{
    [Fact]
    public void Small_resize_does_not_stop_the_session_host_or_switch_to_sequential()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var shell = new TgSessionHostWindow();
        var page = TgChatWindow.Build(); shell.AddPage(page); shell.Activate(page);
        var stage = 0; var complete = false;
        app.AddTimeout(TimeSpan.FromMilliseconds(250), () =>
        {
            switch (stage++)
            {
                case 0: page.SetHostedApplication(app); app.Driver!.SetScreenSize(18, 10); return true;
                case 1: return true;
                default:
                    Assert.True(page.IsSessionPage);
                    Assert.Same(shell, app.TopRunnableView);
                    page.SetHostedApplication(null); complete = true; app.RequestStop(); return false;
            }
        });
        app.Run(shell); Assert.True(complete);
    }

    [Fact]
    public void Hidden_transcript_defers_wrap_work_until_its_session_is_selected()
    {
        using var shell = new TgSessionHostWindow();
        var pageA = TgChatWindow.Build(); var pageB = TgChatWindow.Build();
        shell.AddPage(pageA); shell.AddPage(pageB); shell.Activate(pageB);
        pageA.Transcript.Width = 40; pageA.Transcript.Height = 20; pageA.Transcript.Layout();
        for (var row = 0; row < 100; row++) pageA.Transcript.Append("background line " + row, TranscriptStyle.Prose);
        Assert.Equal(0, pageA.Transcript.FullWrapRebuildCount);
        Assert.Equal(100, pageA.Transcript.RetainedRowCount);
        shell.Activate(pageA);
        Assert.Contains(pageA.Transcript.RenderedRows, row => row.Text.Contains("background line 99", StringComparison.Ordinal));
        Assert.Equal(1, pageA.Transcript.FullWrapRebuildCount);
    }

    [Theory]
    [InlineData(40, 20)] [InlineData(80, 24)] [InlineData(120, 36)]
    public void Overflow_keeps_the_selected_tab_visible_and_the_prompt_inside_its_page(int columns, int rows)
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var shell = new TgSessionHostWindow();
        var page = TgChatWindow.Build(); shell.AddPage(page); shell.Activate(page);
        shell.UpdateTabs(Enumerable.Range(1, 8).Select(index => new TgSessionTab("long session title", "◐", index == 8)).ToArray());
        var stage = 0; var complete = false;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            if (stage++ == 0) { app.Driver!.SetScreenSize(columns, rows); shell.RefreshPageLayout(true); return true; }
            if (shell.Frame.Width != columns || shell.Frame.Height != rows) return true;
            shell.RefreshPageLayout(true);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(600), () =>
        {
            var screen = TgSnapshot.Capture(app);
            Assert.Contains("8:◐", screen[0]);
            Assert.Equal(1, page.Frame.Y);
            Assert.True(page.Composer.Frame.Bottom <= page.Viewport.Height);
            Assert.True(page.Hints.Frame.Bottom <= page.Viewport.Height);
            Assert.True(page.Composer.Input.HasFocus);
            complete = true; app.RequestStop(); return false;
        });
        app.Run(shell); Assert.True(complete);
    }

    [Fact]
    public void Hidden_session_approval_cannot_consume_the_visible_sessions_input()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var shell = new TgSessionHostWindow();
        var pageA = TgChatWindow.Build(); var pageB = TgChatWindow.Build();
        shell.AddPage(pageA); shell.AddPage(pageB); shell.Activate(pageB);
        var loopB = new TgChatLoop(pageB); var sentB = 0; var answeredA = false;
        loopB.SetPromptHandler((_, _, _) => { sentB++; return Task.CompletedTask; });
        pageA.PanelConfirmed += _ => answeredA = true;
        pageA.Transcript.Append("only A", TranscriptStyle.Prose);
        pageB.Transcript.Append("only B", TranscriptStyle.Prose);
        var stage = 0;
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            switch (stage++)
            {
                case 0:
                    shell.Activate(pageB); loopB.SetViewActive(app);
                    pageA.ShowApproval("synthetic approval in A");
                    pageB.Composer.Input.Text = "send only to B";
                    app.InjectKey(Key.Enter); return true;
                case 1:
                    Assert.Equal(1, sentB); Assert.False(answeredA);
                    var screen = string.Join('\n', TgSnapshot.Capture(app));
                    Assert.DoesNotContain("only A", screen);
                    Assert.DoesNotContain("synthetic approval in A", screen);
                    Assert.Contains("only B", screen);
                    loopB.SetViewActive(null); shell.Activate(pageA);
                    return true;
                case 2:
                    app.InjectKey(Key.Enter); return true;
                default:
                    Assert.True(answeredA);
                    loopB.Stop(); app.RequestStop(); return false;
            }
        });
        app.Run(shell);
        loopB.SetViewActive(null);
        Assert.Equal(4, stage);
    }

    [Fact]
    public void Terminal_paste_targets_the_active_page_without_changing_another_draft()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        TgTerminalPaste.Attach(app);
        using var shell = new TgSessionHostWindow();
        var pageA = TgChatWindow.Build(); var pageB = TgChatWindow.Build();
        shell.AddPage(pageA); shell.AddPage(pageB);
        pageA.Composer.Input.Text = "draft A";
        shell.Activate(pageB);
        app.AddTimeout(TimeSpan.FromMilliseconds(250), () =>
        {
            shell.Activate(pageB);
            Assert.True(app.RaisePasteEvent("pasted B"));
            Assert.Equal("pasted B", pageB.Composer.Input.Text);
            Assert.Equal("draft A", pageA.Composer.Input.Text);
            app.RequestStop(); return false;
        });
        app.Run(shell);
    }
}
