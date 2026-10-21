using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;
using Attr = Terminal.Gui.Drawing.Attribute;

namespace RuAgent.Cli.Tests;

public sealed class TgDocumentBoundsTests
{
    [Fact]
    public void Short_popup_preserves_every_underlying_cell_outside_the_card()
    {
        string[] before = [];
        Attr[][] paint = [];
        Render((app, window, _) =>
            {
                before = TgSnapshot.Capture(app);
                paint = Enumerable.Range(0, app.Driver!.Rows).Select(row =>
                    Enumerable.Range(0, app.Driver.Cols).Select(col => TgSnapshot.CellAttribute(app, row, col)).ToArray()).ToArray();
                window.ShowDocument("status", ["session", "preset standard", "model test"]);
            },
            (app, window, _) =>
            {
                var after = TgSnapshot.Capture(app);
                var frame = window.Document.Frame;
                var cardPaint = TgCard.Attribute(CardRowKind.Item).Background;
                var cardRows = Enumerable.Range(0, app.Driver!.Rows)
                    .Where(row => TgSnapshot.CellAttribute(app, row, frame.X + frame.Width / 2).Background == cardPaint).ToArray();
                Assert.NotEmpty(cardRows);
                for (var row = 0; row < app.Driver.Rows; row++)
                {
                    for (var col = 0; col < app.Driver.Cols; col++)
                    {
                        if (row >= cardRows[0] && row <= cardRows[^1] && col >= frame.X && col < frame.Right) continue;
                        Assert.Equal(col < before[row].Length ? before[row][col] : ' ', col < after[row].Length ? after[row][col] : ' ');
                        Assert.Equal(paint[row][col], TgSnapshot.CellAttribute(app, row, col));
                    }
                }
                Assert.Equal(3 + TuiCard.TuiCardFrameRows, frame.Height);
                Assert.Equal(cardRows[0], frame.Y);
                Assert.Equal(cardRows[^1] + 1, frame.Bottom);
                app.InjectKey(Key.Esc);
            },
            (app, window, _) =>
            {
                Assert.False(window.IsDocumentPanelOpen);
                Assert.Equal(before, TgSnapshot.Capture(app));
            });
    }

    [Fact]
    public void Resize_remeasures_wrapped_height_and_reclaims_the_previous_bounds()
    {
        var source = "START" + new string('x', 190) + "END";
        Render((_, window, _) => window.ShowDocument("output", [source]),
            (_, window, resize) =>
            {
                Assert.Equal(8, window.Document.Frame.Height);
                resize(44, 36);
            },
            (app, window, resize) =>
            {
                Assert.Equal(44, app.Driver!.Cols);
                Assert.Equal(12, window.Document.Frame.Height);
                Assert.Contains("END", string.Join('\n', TgSnapshot.Capture(app)));
                resize(100, 36);
            },
            (app, window, _) =>
            {
                Assert.Equal(100, app.Driver!.Cols);
                Assert.Equal(8, window.Document.Frame.Height);
                Assert.Contains("END", string.Join('\n', TgSnapshot.Capture(app)));
            });
    }

    [Fact]
    public void Long_document_keeps_scrolling_and_reused_short_content_shrinks_the_view()
    {
        Render((_, window, _) => window.ShowDocument("long", Enumerable.Range(0, 100).Select(i => $"line {i:000}").ToArray()),
            (_, window, _) =>
            {
                Assert.Equal(34, window.Document.Frame.Height);
                window.Document.ScrollTo(int.MaxValue);
            },
            (app, window, _) =>
            {
                Assert.Contains("line 099", string.Join('\n', TgSnapshot.Capture(app)));
                window.Document.SetContent("short", ["one"]);
            },
            (app, window, _) =>
            {
                Assert.Equal(6, window.Document.Frame.Height);
                Assert.Equal(0, window.Document.Offset);
                Assert.Contains("one", string.Join('\n', TgSnapshot.Capture(app)));
                Assert.DoesNotContain("line 099", string.Join('\n', TgSnapshot.Capture(app)));
            });
    }

    private static void Render(params Action<IApplication, TgChatWindow, Action<int, int>>[] frames)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        for (var i = 0; i < 50; i++) window.Transcript.Append($"backdrop {i:00} " + new string('x', 110), TranscriptStyle.Prose);
        window.Composer.Input.Text = "draft " + new string('d', 60);
        var index = -2;
        var targetWidth = 100;
        var targetHeight = 36;
        void Resize(int width, int height)
        {
            targetWidth = width;
            targetHeight = height;
            app.Driver!.SetScreenSize(width, height);
            window.Frame = new System.Drawing.Rectangle(0, 0, width, height);
        }
        Exception? failure = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(180), () =>
        {
            try
            {
                if (index == -2)
                {
                    Resize(100, 36);
                    window.FocusComposer();
                    index++;
                    return true;
                }
                if (app.Driver!.Cols != targetWidth || app.Driver.Rows != targetHeight
                    || window.Viewport.Width != targetWidth || window.Viewport.Height != targetHeight)
                {
                    Resize(targetWidth, targetHeight);
                    return true;
                }
                if (index == -1)
                {
                    index++;
                    return true;
                }
                frames[index++](app, window, Resize);
                if (index < frames.Length) return true;
            }
            catch (Exception error) { failure = error; }
            app.RequestStop();
            return false;
        });
        app.AddTimeout(TimeSpan.FromSeconds(6), () => { app.RequestStop(); return false; });
        app.Run(window);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(frames.Length, index);
    }
}
