using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Xunit;
using Terminal.Gui.Testing;

namespace RuAgent.Cli.Tests;

public sealed class TgSlashTypingLayoutTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Typing_partial_model_and_backspacing_keeps_the_painted_card_adjacent_to_input(bool chat, bool hosted)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var page = TgChatWindow.Build();
        using var shell = new TgSessionHostWindow();
        if (hosted) { shell.AddPage(page); shell.Activate(page); }
        if (chat) page.Transcript.Append("Existing conversation", TranscriptStyle.Prose);
        var loop = new TgChatLoop(page);
        const int columns = 94, rows = 28;
        var expected = new[] { "/", "/m", "/mo", "/mod", "/mode", "/model", "/mode", "/mod", "/mo", "/m", "/", "" };
        var step = -1;
        Exception? failure = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(120), () =>
        {
            try
            {
                if (app.AnsiStartupGate is { IsReady: false }) return true;
                if (app.Driver!.Cols != columns || app.Driver.Rows != rows)
                {
                    app.Driver.SetScreenSize(columns, rows);
                    if (hosted) shell.RefreshPageLayout(true); else page.RefreshSessionLayout();
                    return true;
                }
                if (step < 0)
                {
                    page.SetHostedApplication(app);
                    loop.SetViewActive(app);
                    page.FocusComposer();
                    app.InjectKey(new Key('/'));
                    step = 0;
                    return true;
                }
                if (hosted) shell.RefreshPageLayout(); // Production skips stable-size relayout.
                page.TickWelcome();
                Assert.Equal(expected[step], page.Composer.Input.Text);
                if (expected[step].Length > 0)
                {
                    var dropdown = Assert.Single(page.SubViews.OfType<TgSlashDropdown>());
                    Assert.True(page.IsSlashDropdownOpen);
                    Assert.Equal(page.Composer.Frame.Y, dropdown.Frame.Bottom);
                    Assert.Equal(Math.Min(dropdown.PanelHeight, Math.Max(3, page.Composer.Frame.Y)), dropdown.Frame.Height);
                    var screen = TgSnapshot.Capture(app);
                    var footer = page.Frame.Y + dropdown.Frame.Bottom - 1;
                    Assert.Contains("esc close", screen[footer]);
                }
                else Assert.False(page.IsSlashDropdownOpen);

                step++;
                if (step == expected.Length) { app.RequestStop(); return false; }
                app.InjectKey(step < 6 ? new Key("model"[step - 1]) : Key.Backspace);
                return true;
            }
            catch (Exception error) { failure = error; app.RequestStop(); return false; }
        });
        app.AddTimeout(TimeSpan.FromSeconds(7), () => { app.RequestStop(); return false; });
        if (hosted) app.Run(shell); else app.Run(page);
        loop.SetViewActive(null);
        page.SetHostedApplication(null);
        loop.Stop();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(expected.Length, step);
    }
}
