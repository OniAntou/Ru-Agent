using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Protocol.Harness;
using Terminal.Gui.App;
using Xunit;
using TextStyle = Terminal.Gui.Drawing.TextStyle;

namespace RuAgent.Cli.Tests;

public sealed class TgTranscriptWriterDispatchTests
{
    [Fact]
    public void Deferred_batch_keeps_rows_styles_reasoning_and_todos_until_applied()
    {
        using IApplication app = Application.Create();
        using var view = new TgTranscriptView { Frame = new(0, 0, 100, 30) };
        List<Action> pending = [];
        IReadOnlyList<AgentTodoWire>? publishedTodos = null;
        var writer = new TgTranscriptWriter(app, view, todos => publishedTodos = todos, pending.Add);
        AgentTodoWire[] todos = [new("Verify replay", AgentTodoStatuses.InProgress)];

        writer.WriteLine("Imported report");
        writer.AppendPresentation(new TranscriptLine([
            new("Status "), new("ready", TuiToken.Brand, Bold: true)], TranscriptRole.Meta));
        writer.AppendActivityPresentation("act_read", TranscriptLine.Plain("Reading file", TranscriptRole.Tool));
        writer.UpdateActivityPresentation("act_read", TranscriptLine.Plain("Read complete", TranscriptRole.Tool, TuiToken.Ok));
        writer.RetainDetails(["Full reasoning detail"]);
        Assert.True(((ITranscriptTodoWriter)writer).TryWriteTodos(todos));

        Assert.Empty(view.Lines);
        Assert.Empty(view.CollapsedRows);
        Assert.Null(publishedTodos);
        Assert.NotEmpty(pending);

        foreach (var action in pending) action();

        Assert.Equal(new[] { "Imported report", "Status ready", "Read complete" }, view.Lines.Select(row => row.Text));
        Assert.Equal(TranscriptStyle.Meta, view.Lines[1].Style);
        var emphasis = Assert.Single(view.Lines[1].Spans!, span => span.Text == "ready");
        Assert.Equal(TextStyle.Bold, emphasis.Attr.Style);
        Assert.Equal(TgPalette.Attr(TuiToken.Brand).Foreground, emphasis.Attr.Foreground);
        Assert.Equal(TranscriptStyle.Tool, view.Lines[2].Style);
        Assert.Equal(TgPalette.Attr(TuiToken.Ok), Assert.Single(view.Lines[2].Spans!).Attr);
        Assert.Contains(view.CollapsedRows, row => row.Text == "Full reasoning detail");
        Assert.Same(todos, publishedTodos);
    }

    [Fact]
    public async Task Deferred_assistant_replacement_preserves_prior_rows_tool_rows_and_rich_spans()
    {
        using IApplication app = Application.Create();
        using var view = new TgTranscriptView { Frame = new(0, 0, 100, 30) };
        List<Action> pending = [];
        var writer = new TgTranscriptWriter(app, view, dispatcher: pending.Add);
        view.Append("Earlier answer", TranscriptStyle.Prose);
        writer.BeginAssistantTurn();
        writer.CommitPresentation([TranscriptLine.Plain("Incomplete answer")]);
        writer.AppendActivityPresentation("act_test", TranscriptLine.Plain("Tests passed", TranscriptRole.Tool, TuiToken.Ok));

        Assert.Equal("Earlier answer", Assert.Single(view.Lines).Text);
        foreach (var action in pending) action();
        pending.Clear();

        var replacement = writer.ReplaceAssistantPresentationAsync([
            new TranscriptLine([new("Complete "), new("answer", TuiToken.Bright, Bold: true)])]);

        Assert.False(replacement.IsCompleted);
        Assert.Contains(view.Lines, row => row.Text == "Incomplete answer");
        Assert.DoesNotContain(view.Lines, row => row.Text == "Complete answer");

        foreach (var action in pending) action();

        Assert.True(await replacement);
        Assert.Contains(view.Lines, row => row.Text == "Earlier answer");
        Assert.Contains(view.Lines, row => row.Text == "Tests passed" && row.Style == TranscriptStyle.Tool);
        Assert.DoesNotContain(view.Lines, row => row.Text == "Incomplete answer");
        var answer = Assert.Single(view.Lines, row => row.Text == "Complete answer");
        Assert.Contains(answer.Spans!, span => span.Text == "answer" && span.Attr.Style == TextStyle.Bold);
    }
}
