using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgSlashDropdownLayoutTests
{
    [Theory]
    [InlineData(false, 88, 24, false)]
    [InlineData(true, 88, 24, false)]
    [InlineData(false, 180, 40, false)]
    [InlineData(true, 180, 40, false)]
    [InlineData(false, 20, 12, false)]
    [InlineData(false, 88, 24, true)]
    [InlineData(true, 180, 40, true)]
    public void Filtering_and_resizing_keep_the_dropdown_against_the_composer(bool chat, int columns, int rows, bool hosted)
    {
        var expectedColumns = columns; var expectedRows = rows;
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        using var shell = new TgSessionHostWindow();
        if (hosted)
        {
            shell.AddPage(window); shell.Activate(window);
            shell.UpdateTabs([new("A", "idle", true), new("B", "idle", false)]);
        }
        if (chat) window.Transcript.Append("Existing conversation", TranscriptStyle.Prose);
        var step = 0; Exception? failure = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(120), () =>
        {
            try
            {
                // Like the image-preview fixture, wait for the ANSI driver to retain
                // the injected size before inspecting a painted frame.
                if (app.AnsiStartupGate is { IsReady: false }) return true;
                if (step > 0 && (app.Driver!.Cols != expectedColumns || app.Driver.Rows != expectedRows))
                {
                    Resize(expectedColumns, expectedRows);
                    return true;
                }
                switch (step)
                {
                    case 0:
                        window.SetHostedApplication(app);
                        Resize(columns, rows);
                        window.FocusComposer(); step++; return true;
                    case 1:
                        window.Composer.Input.Text = "/"; step++; return true;
                    case 2:
                        AssertAnchored();
                        window.Composer.Input.Text = "/model"; step++; return true;
                    case 3:
                        AssertAnchored();
                        window.Composer.Input.Text = "/"; step++; return true;
                    case 4:
                        AssertAnchored();
                        window.Composer.Input.Text = "/model";
                        Resize(Math.Max(20, columns - 12), Math.Max(12, rows - 6));
                        step++; return true;
                    case 5:
                        AssertAnchored();
                        Resize(columns + 12, rows + 6);
                        step++; return true;
                    default:
                        AssertAnchored(); step++; app.RequestStop(); return false;
                }
            }
            catch (Exception error) { failure = error; app.RequestStop(); return false; }
        });
        app.AddTimeout(TimeSpan.FromSeconds(5), () => { app.RequestStop(); return false; });
        if (hosted) app.Run(shell); else app.Run(window);
        window.SetHostedApplication(null);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(7, step);

        void Resize(int width, int height)
        {
            expectedColumns = width; expectedRows = height;
            app.Driver!.SetScreenSize(width, height);
            if (hosted) shell.RefreshPageLayout(true);
            else window.RefreshSessionLayout();
        }

        void AssertAnchored()
        {
            Assert.Equal(expectedColumns, app.Driver!.Cols);
            Assert.Equal(expectedRows, app.Driver.Rows);
            var dropdown = Assert.Single(window.SubViews.OfType<TgSlashDropdown>());
            Assert.True(window.IsSlashDropdownOpen);
            Assert.Equal(window.Composer.Frame.Y, dropdown.Frame.Bottom);
            Assert.Equal(window.Composer.Frame.X, dropdown.Frame.X);
            Assert.Equal(window.Composer.Frame.Width, dropdown.Frame.Width);
            Assert.True(dropdown.Frame.Y >= 0);
            Assert.Equal(Math.Min(dropdown.PanelHeight, Math.Max(3, window.Composer.Frame.Y)), dropdown.Frame.Height);
            var top = window.Frame.Y + window.Composer.Frame.Y;
            var left = window.Frame.X + window.Composer.Frame.X;
            Assert.Equal(TgPalette.Attr(TuiToken.Ink, TuiPalette.SurfaceRgb()).Background,
                TgSnapshot.CellAttribute(app, top, left + 1).Background);
        }
    }

}
