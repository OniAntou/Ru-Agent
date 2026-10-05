using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgDocumentPanelTests
{
    private static IReadOnlyList<string> Report(int rows) =>
        Enumerable.Range(1, rows).Select(i => $"line {i:D2} of the report").ToList();

    /// <summary>Runs the window, driving the document from a timeout on the UI loop.</summary>
    private static void Drive(Action<TgChatWindow, IApplication> script)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            window.FocusComposer();
            script(window, app);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(600), () =>
        {
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    private static void AssertOnScreen(IApplication app, string expected) =>
        Assert.Contains(expected, string.Join('\n', TgSnapshot.Capture(app)), StringComparison.Ordinal);

    [Fact]
    public void ReportCommand_OpensTheWindowOverTheTranscript()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            window.FocusComposer();
            window.ShowDocument("status", Report(60));
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            Assert.True(window.IsDocumentPanelOpen);
            Assert.True(window.Document.HasFocus);

            // The report is a window of its own, not rows in the transcript: the report text
            // and the panel's own hint are on screen at the same time.
            AssertOnScreen(app, "line 01 of the report");
            AssertOnScreen(app, "esc close");
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void Escape_ClosesTheWindowAndReturnsTheKeysToTheComposer()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            window.FocusComposer();
            window.ShowDocument("status", Report(60));
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            app.InjectKey(Key.Esc);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(600), () =>
        {
            Assert.False(window.IsDocumentPanelOpen);
            Assert.True(window.Composer.Input.HasFocus);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void ScrollKeys_WalkThroughTheRows()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            window.FocusComposer();
            window.ShowDocument("status", Report(600));
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            Assert.Equal(0, window.Document.Offset);
            app.InjectKey(Key.CursorDown);
            app.InjectKey(Key.CursorDown);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(450), () =>
        {
            Assert.Equal(2, window.Document.Offset);
            AssertOnScreen(app, "line 03 of the report");

            // End jumps to the tail, so the last row of a long report is always reachable.
            app.InjectKey(Key.End);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(700), () =>
        {
            var screen = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Contains("line 600 of the report", screen, StringComparison.Ordinal);
            Assert.DoesNotContain("line 01 of the report", screen, StringComparison.Ordinal);

            // And Home comes back to the top.
            app.InjectKey(Key.Home);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(850), () =>
        {
            Assert.Equal(0, window.Document.Offset);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void ColumnAlignedOutput_KeepsItsShape()
    {
        // /tree and /status align their columns with runs of spaces. Wrapping on word
        // boundaries would collapse those and make the tree unreadable.
        var body = new[] { "root", "├─ child one   │ ok", "│   └─ leaf   │ ok", "└─ child two   │ ok" };
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            window.FocusComposer();
            window.ShowDocument("tree", body);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            AssertOnScreen(app, "├─ child one   │ ok");
            AssertOnScreen(app, "│   └─ leaf   │ ok");
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void OpeningTheWindow_DoesNotLeaveTheSlashListBehind()
    {
        Drive((window, _) =>
        {
            window.FocusComposer();
            window.Composer.Input.Text = "/";
            window.ShowDocument("status", Report(10));
        });
    }
}