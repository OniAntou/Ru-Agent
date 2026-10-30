using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgWorkspaceHeadingTests
{
    [Fact]
    public void Non_git_folder_is_a_workspace_and_path_spaces_are_preserved()
    {
        Assert.Equal(@"C:\work\My  Project", TuiWorkspaceHeading.DisplayPath(@"C:\work\My  Project"));
        Assert.DoesNotContain('\u001b', TuiWorkspaceHeading.DisplayPath("C:\\work\\\u001b[31mproject\u001b[0m"));
    }

    [Fact]
    public void Parsing_retains_cwd_without_claiming_an_explicit_workspace()
    {
        var implicitChat = Assert.IsType<ChatCommand>(AgentCliParser.Parse(["agent", "chat"]).Command);
        var explicitChat = Assert.IsType<ChatCommand>(AgentCliParser.Parse(["agent", "chat", "--workspace", Environment.CurrentDirectory]).Command);
        Assert.False(implicitChat.WorkspaceExplicit); Assert.True(explicitChat.WorkspaceExplicit);
        Assert.Equal(Environment.CurrentDirectory, implicitChat.WorkspacePath);
        Assert.Equal(implicitChat.WorkspacePath, explicitChat.WorkspacePath);
        Assert.Equal(implicitChat.Preset, explicitChat.Preset);
    }

    [Theory]
    [InlineData(80, false)]
    [InlineData(120, false)]
    [InlineData(160, true)]
    public void Workspace_is_absent_with_permission_hidden_only_on_welcome(int columns, bool chat)
    {
        const string path = @"C:\work\Ru-Agent";
        Render(columns, 30, chat, w => w.Hints.SetIdle(path), (app, w, screen) =>
        {
            var row = screen[w.Header.Frame.Y];
            Assert.DoesNotContain(path, row);
            Assert.DoesNotContain("Chưa chọn dự án", row);
            Assert.True(w.Header.Visible); Assert.Equal(!chat, w.Landing.Visible);
            if (!chat) { Assert.DoesNotContain("standard", row); Assert.DoesNotContain("39,413", row); }
            else Assert.Contains("standard", row);
            Assert.DoesNotContain("Ready", string.Join('\n', screen));
        });
    }

    [Theory]
    [InlineData(20, 12)]
    [InlineData(32, 20)]
    [InlineData(80, 24)]
    public void Long_unicode_path_and_resize_preserve_the_draft_and_permission(int width, int height)
    {
        Render(width, height, true, w =>
        {
            w.Hints.SetIdle(@"C:\Users\tester\Dự án\" + new string('界', 80) + @"\Ru-Agent");
            w.Header.SetTitle(new string('t', 120)); w.Composer.Input.Text = "draft";
        }, (app, w, screen) =>
        {
            Assert.Equal("draft", w.Composer.Input.Text); Assert.Equal("standard", w.Header.Preset);
            var headerRows = screen.Skip(w.Header.Frame.Y).Take(w.Header.Frame.Height).ToArray();
            Assert.Contains("standard", headerRows[0]);
            Assert.DoesNotContain("Dự án", string.Join('\n', headerRows));
            Assert.DoesNotContain("Ru-Agent", string.Join('\n', headerRows));
            Assert.DoesNotContain('\u001b', string.Join('\n', headerRows));
            // Capture includes a blank continuation cell after each wide glyph. Its
            // string must not be measured as if those placeholders were new columns.
            Assert.All(headerRows, row => Assert.True(row.Length <= width));
            Assert.True(w.Transcript.Frame.Y >= w.Header.Frame.Bottom);
        });
    }

    [Fact]
    public void Attachment_feedback_and_transcript_reset_do_not_restore_workspace_heading()
    {
        Render(120, 30, true, w =>
        {
            w.Hints.SetIdle("[1 attachment] old workspace");
            w.ResetTranscript();
        }, (_, w, screen) =>
        {
            Assert.DoesNotContain("Chưa chọn dự án", screen[w.Header.Frame.Y]);
            Assert.DoesNotContain("old workspace", screen[w.Header.Frame.Y]);
            Assert.DoesNotContain("standard", screen[w.Header.Frame.Y]);
            Assert.True(w.Landing.Visible);
        });
    }

    [Theory]
    [InlineData(24, true)]
    [InlineData(60, true)]
    [InlineData(32, false)]
    public void Status_shows_all_path_chunks_without_truncation(int width, bool framed)
    {
        var path = @"C:\Users\tester\Documents\" + new string('x', 150) + @"\Ru-Agent";
        var snapshot = new TuiStatusSnapshot("s", "standard", "b", "m", path, 0, null, 0, null);
        var rows = TuiSurfaces.Status(width, snapshot, framed).Select(TuiText.Strip).ToArray();
        var chunks = TuiText.HardWrapLine(path, width - (framed ? 6 : 2));
        foreach (var chunk in chunks) Assert.Contains(rows, row => row.Contains(chunk, StringComparison.Ordinal));
        if (framed) Assert.All(rows, row => Assert.Equal(width, TuiText.VisibleWidth(row)));
    }

    private static void Render(int columns, int rows, bool chat, Action<TgChatWindow> configure,
        Action<IApplication, TgChatWindow, string[]> inspect)
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Composer.SetMeta("standard", "model", "provider"); window.Header.UpdateUsage(39413, 20, 0.29m);
        if (chat) window.Transcript.Append("active conversation", TranscriptStyle.Prose);
        configure(window);
        Exception? failure = null; var finished = false;
        var resizeClock = System.Diagnostics.Stopwatch.StartNew();
        var stable = false;
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            var sizeMatches = app.Driver!.Cols == columns && app.Driver.Rows == rows
                && window.Frame.Width == columns && window.Frame.Height == rows;
            if (resizeClock.Elapsed < TimeSpan.FromSeconds(3) && (!sizeMatches || !stable))
            {
                // A delayed ANSI startup resize can overwrite the requested size.
                // Reapply it, then allow a complete stable paint before inspecting.
                if (!sizeMatches) app.Driver.SetScreenSize(columns, rows);
                stable = sizeMatches;
                return true;
            }
            try
            {
                Assert.Equal(columns, app.Driver.Cols); Assert.Equal(rows, app.Driver.Rows);
                Assert.Equal(columns, window.Frame.Width); Assert.Equal(rows, window.Frame.Height);
                inspect(app, window, TgSnapshot.Capture(app)); finished = true;
            }
            catch (Exception ex) { failure = ex; }
            app.RequestStop(); return false;
        });
        app.Run(window);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(finished);
    }
}
