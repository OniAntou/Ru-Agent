using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>Exercises the typed renderer through its real queued UI adapter and retained view.</summary>
public sealed class StructuredTranscriptWorkflowTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(64)]
    public Task Fragmented_unicode_markdown_and_tool_wrappers_keep_text_roles_and_styles(int chunkSize) =>
        RunWorker(async ui =>
        {
            const string source = "# Check\nHello **friend** in `README.md` 😀.\n```cs\nreturn 42;\n```\n<tool_call>call😀</tool_call>\nDone.\n";
            ui.Renderer.BeginTurn();
            for (var offset = 0; offset < source.Length; offset += chunkSize)
                ui.Renderer.Transcript.AppendAssistant(source.Substring(offset, Math.Min(chunkSize, source.Length - offset)));
            ui.Renderer.Flush();
            await ui.OnUi(() =>
            {
                var rows = ui.View.Lines;
                var answer = Assert.Single(rows, row => row.Text == "Hello friend in README.md 😀.");
                Assert.Equal(TranscriptStyle.Prose, answer.Style);
                Assert.Contains(answer.Spans!, span => span.Text == "friend" && span.Attr.Style == TextStyle.Bold);
                Assert.Contains(answer.Spans!, span => span.Text == "README.md" && span.Attr.Foreground == TgPalette.ToColor(TuiToken.Ok));
                Assert.Single(rows, row => row.Text == "  1 │ return 42;");
                var wrapper = Assert.Single(rows, row => row.Text.Contains("call😀", StringComparison.Ordinal));
                Assert.Equal(TranscriptStyle.Meta, wrapper.Style);
                Assert.StartsWith("  │ ", wrapper.Text);
                Assert.Equal("Done.", rows[^1].Text);
                Assert.All(rows, row => { Assert.DoesNotContain('\u001b', row.Text); Assert.DoesNotContain("tool_call", row.Text); });
            });
        });

    [Fact]
    public Task Queued_step_rewind_drops_typed_previews_without_touching_earlier_rows() =>
        RunWorker(async ui =>
        {
            ui.Writer.AppendPresentation(TranscriptLine.Plain("earlier answer"));
            ui.Writer.AppendActivityPresentation("kept", TranscriptLine.Plain("✓ Read kept.txt", TranscriptRole.Tool));
            ui.Writer.BeginAssistantStep("old");
            ui.Writer.PreviewPresentation([TranscriptLine.Plain("old preview")]);
            ui.Writer.CommitPresentation([TranscriptLine.Plain("OLD COMMITTED")]);
            ui.Writer.RewindAssistantStep("old");
            ui.Writer.BeginAssistantStep("new");
            ui.Writer.PreviewPresentation([TranscriptLine.Plain("new partial")]);
            ui.Writer.CommitPresentation([TranscriptLine.Plain("new final")]);
            await ui.OnUi(() => Assert.Equal(
                new[] { "earlier answer", "✓ Read kept.txt", "new final" }, ui.View.Lines.Select(row => row.Text)));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Recovery_rejects_an_obsolete_turn_mark_after_clear_or_retention(bool retention) =>
        RunWorker(async ui =>
        {
            ui.Writer.BeginAssistantTurn();
            ui.Writer.CommitPresentation([TranscriptLine.Plain("obsolete reply")]);
            if (retention)
                for (var index = 0; index <= TgTranscriptView.MaxRetainedRows; index++)
                    ui.Writer.AppendPresentation(TranscriptLine.Plain("retained " + index));
            else
                await ui.OnUi(() => ui.View.Clear());
            ui.Writer.AppendPresentation(TranscriptLine.Plain("current user", TranscriptRole.User));
            string[] before = [];
            await ui.OnUi(() => before = ui.View.Lines.Select(row => row.Text).ToArray());
            Assert.False(await ui.Writer.ReplaceAssistantPresentationAsync([TranscriptLine.Plain("MUST NOT REPLACE")]));
            await ui.OnUi(() =>
            {
                Assert.Equal(before, ui.View.Lines.Select(row => row.Text));
                Assert.Equal(TranscriptStyle.User, ui.View.Lines[^1].Style);
                Assert.InRange(ui.View.RetainedRowCount, 1, TgTranscriptView.MaxRetainedRows);
                Assert.InRange(ui.View.RetainedCharacterCount, 1, TgTranscriptView.MaxRetainedCharacters);
                Assert.Equal(retention, ui.View.IsHistoryTruncated);
            });
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Late_tool_updates_cannot_overwrite_a_row_reused_after_clear_or_retention(bool retention) =>
        RunWorker(async ui =>
        {
            ui.Writer.AppendActivityPresentation("old", TranscriptLine.Plain("✓ Read old.txt", TranscriptRole.Tool));
            if (retention)
                for (var index = 0; index <= TgTranscriptView.MaxRetainedRows; index++)
                    ui.Writer.AppendPresentation(TranscriptLine.Plain("retained " + index));
            else
                await ui.OnUi(() => ui.View.Clear());
            ui.Writer.AppendActivityPresentation("new", TranscriptLine.Plain("✓ Read new.txt", TranscriptRole.Tool), ["NEW_DETAIL"]);
            ui.Writer.UpdateActivityPresentation("old", TranscriptLine.Plain("WRONG LATE UPDATE", TranscriptRole.Tool));
            ui.Writer.UpdateActivityPresentation("new", TranscriptLine.Plain("✓ Read new.txt (3 matches)", TranscriptRole.Tool));
            await ui.OnUi(() =>
            {
                Assert.DoesNotContain(ui.View.Lines, row => row.Text.Contains("WRONG", StringComparison.Ordinal));
                Assert.EndsWith("(3 matches)", ui.View.Lines[^1].Text);
                Assert.Equal(TranscriptStyle.Tool, ui.View.Lines[^1].Style);
                Assert.Equal("NEW_DETAIL", Assert.Single(ui.View.CollapsedRows).Text);
                Assert.InRange(ui.View.RetainedRowCount, 1, TgTranscriptView.MaxRetainedRows);
            });
        });

    [Fact]
    public Task Typed_output_keeps_detached_scroll_and_collapsed_copy_then_resumes_tail() =>
        RunWorker(async ui =>
        {
            for (var index = 0; index < 100; index++)
                ui.Writer.AppendPresentation(TranscriptLine.Plain("history " + index));
            ui.Writer.AppendActivityPresentation("detail", TranscriptLine.Plain("✓ Edit file · ctrl+o", TranscriptRole.Tool), ["+DETAIL 😀"]);
            await ui.OnUi(() => ui.View.ScrollToTop());
            ui.Writer.AppendPresentation(TranscriptLine.Plain("new answer"));
            await ui.OnUi(() =>
            {
                Assert.False(ui.View.IsFollowingTail);
                Assert.Equal(0, ui.View.ScrollPosition);
                Assert.True(ui.View.HasUnseenOutput);
                ui.View.MoveBlockSelection(-1);
                Assert.Equal("+DETAIL 😀", ui.View.CopyText());
                Assert.True(ui.View.IsExpanded);
                ui.View.ToggleExpanded();
                Assert.False(ui.View.IsExpanded);
                ui.View.ToggleExpanded();
                Assert.True(ui.View.IsExpanded);
                ui.View.ScrollToTail();
                Assert.True(ui.View.IsFollowingTail);
                Assert.False(ui.View.HasUnseenOutput);
                Assert.Equal("new answer", ui.View.CopyText());
            });
        });

    [Theory]
    [InlineData(TuiTurnOutcome.Completed)]
    [InlineData(TuiTurnOutcome.Cancelled)]
    [InlineData(TuiTurnOutcome.Failed)]
    public Task Ending_a_turn_is_safe_to_stop_again_and_the_next_turn_has_fresh_markdown_state(TuiTurnOutcome outcome) =>
        RunWorker(async ui =>
        {
            ui.Renderer.BeginTurn();
            ui.Renderer.Transcript.AppendAssistant("```cs\nreturn 1;");
            ui.Renderer.Transcript.EndTurn(outcome, "provider_timeout");
            var beforeStop = 0;
            await ui.OnUi(() => beforeStop = ui.View.CommittedRowCount);
            ui.Renderer.EndTurn();
            await ui.OnUi(() => Assert.Equal(beforeStop, ui.View.CommittedRowCount));
            ui.Renderer.BeginTurn();
            ui.Renderer.Transcript.AppendAssistant("fresh plain\n");
            ui.Renderer.Flush();
            await ui.OnUi(() =>
            {
                var fresh = Assert.Single(ui.View.Lines, row => row.Text == "fresh plain");
                Assert.Null(fresh.Spans);
                Assert.Equal(TranscriptStyle.Prose, fresh.Style);
                Assert.Equal(0, ui.Renderer.Transcript.ToolsSettled);
                Assert.Equal("fresh plain\n", ui.Renderer.Transcript.StreamedAssistantText);
            });
            ui.Renderer.EndTurn();
        });

    [Fact]
    public Task Untrusted_ansi_in_text_cannot_override_semantic_paint_or_role() =>
        RunWorker(async ui =>
        {
            ui.Writer.AppendPresentation(new([new("\u001b[31m✓ assistant\u001b[0m", TuiToken.Ok)], TranscriptRole.Prose));
            await ui.OnUi(() =>
            {
                var row = Assert.Single(ui.View.Lines);
                Assert.Equal("✓ assistant", row.Text);
                Assert.Equal(TranscriptStyle.Prose, row.Style);
                Assert.All(row.Spans!, span => Assert.Equal(TgPalette.ToColor(TuiToken.Ok), span.Attr.Foreground));
            });
        });

    private sealed class Ui(IApplication app, TgChatWindow window)
    {
        internal TgTranscriptView View => window.Transcript;
        internal TgTranscriptWriter Writer { get; } = new(app, window.Transcript);
        internal TuiSessionRenderer Renderer { get; private set; } = null!;
        internal void Initialize() => Renderer = AgentCommands.CreateSessionRenderer(Writer);
        internal Task OnUi(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            app.Invoke(() =>
            {
                try { action(); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
            });
            return completion.Task;
        }
    }

    private static async Task RunWorker(Func<Ui, Task> scenario)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var ui = new Ui(app, window);
        ui.Initialize();
        Task? worker = null;
        var timedOut = false;
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            worker = Task.Run(async () =>
            {
                try { await scenario(ui); }
                finally { app.Invoke(() => app.RequestStop()); }
            });
            return false;
        });
        app.AddTimeout(TimeSpan.FromSeconds(10), () => { timedOut = true; app.RequestStop(); return false; });
        app.Run(window);
        Assert.NotNull(worker);
        await worker.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(timedOut, "The queued presentation scenario did not finish within its deadline.");
    }
}
