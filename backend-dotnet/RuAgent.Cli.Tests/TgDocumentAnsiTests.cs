using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgDocumentAnsiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_reports_have_one_frame_and_keep_unboxed_rows_in_the_transcript(bool help)
    {
        var body = help ? TuiSurfaces.Help(80, framed: false)
            : TuiSurfaces.Status(80, new TuiStatusSnapshot("session-test", "standard", "provider",
                "vision-model", "C:\\workspace", 0, null, 0, true), framed: false);
        Assert.All(body, row =>
        {
            Assert.True(TuiText.VisibleWidth(row) <= 80);
            Assert.DoesNotContain("│", row);
            Assert.DoesNotContain("╭", row);
            Assert.DoesNotContain("╯", row);
        });
        Render((_, window) => window.ShowDocument(help ? "help" : "status", body),
            (app, window) =>
            {
                var screen = string.Join('\n', TgSnapshot.Capture(app));
                Assert.DoesNotContain("│", screen);
                Assert.DoesNotContain("╭", screen);
                Assert.Contains(help ? "/preset" : "standard", screen);
                window.HideDocument();
                foreach (var row in body) window.Transcript.Append(TuiText.Strip(row), TranscriptStyle.Prose);
            },
            (app, _) =>
            {
                var screen = string.Join('\n', TgSnapshot.Capture(app));
                Assert.DoesNotContain("│", screen);
                Assert.DoesNotContain("╭", screen);
            });
    }

    [Theory]
    [InlineData(TuiColorLevel.Basic16)]
    [InlineData(TuiColorLevel.Palette256)]
    [InlineData(TuiColorLevel.TrueColor)]
    public void Status_output_shows_values_without_literal_terminal_escapes(TuiColorLevel level)
    {
        using var color = TuiPalette.PinLevel(level);
        var body = TuiSurfaces.Status(68, new TuiStatusSnapshot(
            "session-test", "standard", "models.dev:openrouter", "vision-model",
            "C:\\project", 2, 1234, 3, true));
        Assert.Contains(body, row => row.Contains('\u001b'));
        Render((_, window) => window.ShowDocument("status", body),
            (app, _) =>
            {
                var screen = string.Join('\n', TgSnapshot.Capture(app));
                Assert.DoesNotContain("\u001b", screen, StringComparison.Ordinal);
                Assert.DoesNotContain("[38;", screen);
                Assert.DoesNotContain("[0m", screen);
                Assert.Contains("session-test", screen);
                Assert.Contains("standard", screen);
                Assert.Contains("models.dev:openrouter", screen);
                Assert.Contains("vision-model", screen);
            });
    }

    [Fact]
    public void Help_output_scrolls_and_closes_without_exposing_ANSI_or_losing_the_draft()
    {
        using var color = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        Render((_, window) =>
            {
                window.Composer.Input.Text = "saved draft";
                window.ShowDocument("help", TuiSurfaces.Help(68));
            },
            (app, window) =>
            {
                var screen = string.Join('\n', TgSnapshot.Capture(app));
                Assert.DoesNotContain("[38;", screen);
                Assert.Contains("/status", screen);
                window.Document.ScrollTo(int.MaxValue);
            },
            (app, window) =>
            {
                Assert.Contains("/exit", string.Join('\n', TgSnapshot.Capture(app)));
                app.InjectKey(Key.Esc);
                Assert.False(window.IsDocumentPanelOpen);
                Assert.Equal("saved draft", window.Composer.Input.Text);
            });
    }

    [Fact]
    public void Wrapped_document_rows_keep_all_characters_instead_of_clipping_each_chunk()
    {
        var source = "START" + new string('x', 75) + "KEEP" + new string('y', 75) + "END";
        Render((_, window) => window.ShowDocument("output", ["\u001b[32m" + source + "\u001b[0m"]),
            (app, _) =>
            {
                var text = string.Concat(TgSnapshot.Capture(app).Select(row => row.Trim()));
                Assert.Contains(source, text);
                Assert.DoesNotContain("[32m", text);
            });
    }

    private static void Render(params Action<IApplication, TgChatWindow>[] frames)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Transcript.Append("Existing conversation", TranscriptStyle.Prose);
        var index = -1;
        Exception? failure = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            try
            {
                if (index < 0 || app.Driver!.Cols != 100 || app.Driver.Rows != 36)
                {
                    app.Driver!.SetScreenSize(100, 36);
                    index = Math.Max(0, index);
                    return true;
                }
                frames[index++](app, window);
                if (index < frames.Length) return true;
            }
            catch (Exception error) { failure = error; }
            app.RequestStop();
            return false;
        });
        app.AddTimeout(TimeSpan.FromSeconds(5), () => { app.RequestStop(); return false; });
        app.Run(window);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(frames.Length, index);
    }
}
