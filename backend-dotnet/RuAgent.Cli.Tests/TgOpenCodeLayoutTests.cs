using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgOpenCodeLayoutTests
{
    [Fact]
    public void EmptyChatHasDefaultPlaceholderBottomPaddingAndQuietCommandHint()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Composer.SetMeta("Standard", "step-5-preview", "kilo");
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rows = TgSnapshot.Capture(app);
            var top = window.Composer.Frame.Y;
            Assert.Contains("Ask anything…", rows[top + 1]);
            Assert.Contains("Standard", rows[top + 3]);
            Assert.DoesNotContain("Standard", rows[top + 4]);
            Assert.EndsWith("ctrl+p commands", rows[window.Hints.Frame.Y]);
            Assert.DoesNotContain("tab complete", rows[window.Hints.Frame.Y]);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
    private static void Render(int columns, int rows, bool session, Action<TgChatWindow> configure,
        Action<IApplication, TgChatWindow, string[]> inspect)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Composer.SetMeta("Standard", "step-5-preview", "kilo");
        window.Composer.SetPlaceholder("Ask anything, / for commands…");
        window.Hints.SetIdle("D:\\LLM");
        window.Header.UpdateUsage(39413, 20, 0.29m);
        if (session) window.Transcript.Append("Inspect the parser", TranscriptStyle.Prose);
        configure(window);
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            app.Driver!.SetScreenSize(columns, rows);
            window.FocusComposer();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(350), () =>
        {
            Assert.Equal(columns, app.Driver!.Cols);
            Assert.Equal(rows, app.Driver.Rows);
            inspect(app, window, TgSnapshot.Capture(app));
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void ComposerUsesFilledSurfaceAndRuAccentWithoutABoxOutline()
    {
        Render(120, 30, true, _ => {}, (app, w, rows) =>
        {
            var top = w.Composer.Frame.Y;
            var left = w.Composer.Frame.X;
            Assert.DoesNotContain("┌", rows[top], StringComparison.Ordinal);
            Assert.Equal(TgPalette.ToColor(TuiPalette.SurfaceRgb()), TgSnapshot.CellAttribute(app, top + 1, left + 20).Background);
            Assert.Equal(TgPalette.ToColor(TuiToken.Ok), TgSnapshot.CellAttribute(app, top + 1, left).Foreground);
            Assert.Contains("Standard", rows[top + TgComposerView.MetadataRow], StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CompactComposerKeepsABlockCursorAfterFocusAndEditing()
    {
        Render(120, 30, true, w => w.Composer.Input.Text = "Hello", (_, w, _) =>
        {
            w.Composer.Input.InsertionPoint = 2;
            Assert.Equal(CursorStyle.SteadyBlock, w.Composer.Input.Cursor.Style);
            Assert.Equal(5, w.Composer.Frame.Height);
        });
    }

    [Fact]
    public void WideSessionUsesTheFullConversationWidthWithContextInTheHeader()
    {
        Render(140, 36, true, _ => {}, (app, w, rows) =>
        {
            var text = string.Join('\n', rows);
            Assert.Contains("D:\\LLM", rows[1], StringComparison.Ordinal);
            Assert.DoesNotContain("Context", text, StringComparison.Ordinal);
            Assert.Contains("39,413", text, StringComparison.Ordinal);
            Assert.True(w.Transcript.Frame.X >= 2);
            Assert.Equal(app.Driver!.Cols - 2, w.Composer.Frame.Right);
            Assert.Equal(4, w.Transcript.Frame.Y);
        });
    }

    [Fact]
    public void NarrowSessionKeepsConversationAndComposerInOneColumn()
    {
        Render(80, 24, true, _ => {}, (app, w, rows) =>
        {
            Assert.DoesNotContain("Context", string.Join('\n', rows), StringComparison.Ordinal);
            Assert.Equal(2, w.Transcript.Frame.X);
            Assert.Equal(2, w.Composer.Frame.X);
            Assert.Equal(78, w.Composer.Frame.Right);
            Assert.Contains("step-5-preview", rows[w.Composer.Frame.Y + TgComposerView.MetadataRow], StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 30)]
    [InlineData(160, 40)]
    public void HomeGroupsTheWordmarkAndComposerInTheCentre(int columns, int rows)
    {
        Render(columns, rows, false, _ => {}, (_, w, _) =>
        {
            Assert.InRange(w.Composer.Frame.Width, 20, 80);
            Assert.InRange(Math.Abs(w.Composer.Frame.X - (columns - w.Composer.Frame.Right)), 0, 1);
            Assert.True(w.Composer.Frame.Bottom < rows - 2);
            Assert.False(w.Header.Visible);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommandOverlaysFitAboveTheComposerOnTheSmallestActiveFrame(bool slash)
    {
        Render(20, 12, false, w =>
        {
            if (slash) w.Composer.Input.Text = "/";
            else w.OpenCommandPalette();
        }, (_, w, rows) =>
        {
            Assert.Contains("commands", string.Join('\n', rows), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("┃", rows[w.Composer.Frame.Y]);
            Assert.Contains("Standard", rows[w.Composer.Frame.Y + TgComposerView.MetadataRow]);
        });
    }

    [Fact]
    public void LongMetadataDoesNotOverwriteInputOrUserSurface()
    {
        Render(80, 24, true, w =>
        {
            w.Composer.SetMeta("Standard", new string('界', 100), new string('p', 100));
            w.Composer.Input.Text = "Giữ nguyên câu đang gõ";
            w.Transcript.Append("Short question", TranscriptStyle.User);
        }, (app, w, rows) =>
        {
            Assert.Contains("Giữ nguyên câu đang gõ", rows[w.Composer.Frame.Y + 1]);
            Assert.Contains("Standard", rows[w.Composer.Frame.Y + TgComposerView.MetadataRow]);
            Assert.Equal(TgPalette.ToColor(TuiPalette.SurfaceRgb()),
                TgSnapshot.CellAttribute(app, w.Transcript.Frame.Y + 1, w.Transcript.Frame.Right - 1).Background);
        });
    }
}
