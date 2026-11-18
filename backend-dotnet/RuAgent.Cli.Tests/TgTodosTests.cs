using System.Drawing;
using System.Text.Json;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgTodosTests
{
    private static readonly AgentTodoWire[] Working =
    [new("Inspect files", AgentTodoStatuses.Completed), new("Update the parser", AgentTodoStatuses.InProgress),
        new("Run tests", AgentTodoStatuses.Pending)];

    private static readonly AgentTodoWire[] Done = Working.Select(item => item with { Status = AgentTodoStatuses.Completed }).ToArray();

    [Theory]
    [InlineData(120, true)]
    [InlineData(80, false)]
    public void Live_todos_follow_terminal_width_and_keep_the_composer_full_width(int columns, bool docked)
    {
        Drive(columns, (app, window, renderer) =>
        {
            renderer.BeginTurn();
            renderer.Transcript.AppendAssistant("Working on the parser.");
            renderer.RenderFrame(TodoFrame(Working));
            renderer.Flush();
            Resize(app, window, columns, () =>
            {
                var rows = TgSnapshot.Capture(app);
                Assert.True(window.Todos.Visible);
                Assert.Equal(docked, window.Todos.Docked);
                Assert.Equal(columns - 4, window.Composer.Frame.Width);
                Assert.Contains("Todos 1/3", rows[window.Todos.Frame.Top]);
                Assert.DoesNotContain(window.Transcript.Lines, row => row.Text.Contains("Plan 1/3", StringComparison.Ordinal));
                if (docked)
                {
                    Assert.True(window.Todos.Frame.Left > window.Transcript.Frame.Right);
                    Assert.Equal(window.Transcript.Frame.Top, window.Todos.Frame.Top);
                    Assert.Contains("▸ Update the parser", rows[window.Todos.Frame.Top + 2]);
                    Resize(app, window, 80, () =>
                    {
                        Assert.False(window.Todos.Docked);
                        Assert.Equal(window.Composer.Frame.Width, window.Transcript.Frame.Width);
                        Assert.Contains("Todos 1/3", TgSnapshot.Capture(app)[window.Todos.Frame.Top]);
                        app.RequestStop();
                    });
                }
                else
                {
                    Assert.Equal(window.Composer.Frame.Width, window.Transcript.Frame.Width);
                    Assert.True(window.Todos.Frame.Bottom <= window.Transcript.Frame.Top);
                    app.RequestStop();
                }
            });
        });
    }

    [Fact]
    public void Completed_list_collapses_and_details_open_by_click_or_keyboard_without_editing_the_draft()
    {
        Drive(120, (app, window, renderer) =>
        {
            window.Transcript.Append("A turn with a plan", TranscriptStyle.Prose);
            renderer.RenderFrame(TodoFrame(Done));
            window.Composer.Input.Text = "unsent draft";
            window.Composer.Input.InsertionPoint = 3;
            AfterDraw(app, () =>
            {
                Assert.False(window.Todos.Docked);
                Assert.Equal(window.Composer.Frame.Width, window.Transcript.Frame.Width);
                Assert.Contains("✓ Todos 3/3", TgSnapshot.Capture(app)[window.Todos.Frame.Top]);
                var bounds = window.Todos.ViewportToScreen();
                app.Mouse.RaiseMouseEvent(new Mouse
                {
                    Flags = MouseFlags.LeftButtonClicked,
                    ScreenPosition = new Point(bounds.Left + 4, bounds.Top),
                });
                Assert.True(window.IsDocumentPanelOpen);
                renderer.RenderFrame(TodoFrame(Working));
                AfterDraw(app, () =>
                {
                    var screen = string.Join('\n', TgSnapshot.Capture(app));
                    Assert.Contains("Todos 1/3", screen);
                    Assert.Contains("▸ Update the parser", screen);
                    app.InjectKey(Key.Esc);
                    Assert.False(window.IsDocumentPanelOpen);
                    Assert.True(window.Composer.Input.HasFocus);
                    Assert.Equal("unsent draft", window.Composer.Input.Text);
                    Assert.Equal(3, window.Composer.Input.InsertionPoint);
                    app.InjectKey(Key.T.WithCtrl);
                    Assert.True(window.IsDocumentPanelOpen);
                    app.InjectKey(Key.T.WithCtrl);
                    Assert.False(window.IsDocumentPanelOpen);
                    app.RequestStop();
                });
            });
        });
    }

    [Fact]
    public void History_restores_the_latest_list_and_reset_or_empty_snapshot_clears_it()
    {
        Drive(120, (app, window, renderer) =>
        {
            renderer.BeginTurn();
            renderer.Transcript.ReplayTurn(new AgentRunActivityReplay([Activity(Working), Activity(Done, "act_final")], []),
                ["Done"], null, null, TuiTurnOutcome.Completed, null);
            Assert.Equal("Todos 3/3", window.Todos.ProgressLabel);
            renderer.BeginTurn();
            Assert.True(window.Todos.AllCompleted);
            renderer.RenderFrame(TodoFrame([]));
            Assert.False(window.Todos.HasItems);
            Assert.False(window.Todos.Visible);

            renderer.Transcript.RestoreTodos(new AgentRunActivityReplay([Activity(Working)], []));
            Assert.True(window.Todos.Visible);
            window.ResetTranscript();
            Assert.False(window.Todos.HasItems);
            Assert.False(window.Todos.Visible);
            Assert.True(window.Landing.Visible);
            app.RequestStop();
        });
    }

    private static AgentActivityModel Activity(IReadOnlyList<AgentTodoWire> items, string id = "act_todo") =>
        new(id, AgentActivityKind.Todo, AgentActivityState.Completed, null, null, null, null, items.Count,
            null, null, null, "succeeded", null, null, null, Todos: items);

    private static SseFrame TodoFrame(IReadOnlyList<AgentTodoWire> items)
    {
        var wire = new AgentActivityWire("act_todo", AgentActivityKind.Todo, AgentActivityState.Completed,
            Count: items.Count, Todos: items);
        return new SseFrame(null, "transient", JsonSerializer.Serialize(new
        {
            kind = "agent.activity", data = AgentActivityWireCodec.Serialize(wire),
        }), true, 1);
    }

    private static void Drive(int columns, Action<IApplication, TgChatWindow, TuiSessionRenderer> script)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var renderer = AgentCommands.CreateSessionRenderer(new TgTranscriptWriter(app, window.Transcript, window.SetTodos));
        _ = new TgChatKeyboardBindings(window, () => false, () => false, () => { }, () => { },
            _ => { }, () => { }, () => { }, () => { });
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            Resize(app, window, columns, () =>
            {
                window.FocusComposer();
                script(app, window, renderer);
            });
            return false;
        });
        app.AddTimeout(TimeSpan.FromSeconds(5), () => throw new TimeoutException("Todo UI test did not finish."));
        app.Run(window);
    }

    private static void AfterDraw(IApplication app, Action action) =>
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () => { action(); return false; });

    private static void Resize(IApplication app, TgChatWindow window, int columns, Action ready)
    {
        app.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
        {
            if (app.AnsiStartupGate is { IsReady: false }) return true;
            var driver = app.Driver!;
            if (driver.Cols != columns || driver.Rows != 30)
            {
                driver.SetScreenSize(columns, 30);
                return true;
            }
            // The ANSI size probe can restore the host size between iterations.
            // Layout, draw and inspect in the iteration that observes the requested size.
            window.RefreshSessionLayout();
            app.LayoutAndDraw(true);
            ready();
            return false;
        });
    }
}
