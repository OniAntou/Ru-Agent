using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class StructuredTranscriptWorkflowTests
{
    [Fact]
    public Task Each_activity_row_keeps_its_own_paint_and_inline_result() =>
        RunWorker(async ui =>
        {
            ui.Renderer.BeginTurn();
            ui.Renderer.Transcript.ApplyActivity(InspectionActivity("failed", AgentActivityKind.Edit, "Edit") with
            { State = AgentActivityState.Failed, FileName = "locked.cs", SafeCode = "permission_denied" });
            SettleInspectionActivity(ui.Renderer, 1);
            ui.Renderer.Transcript.ApplyActivity(InspectionActivity("command", AgentActivityKind.Terminal, "Terminal") with
            { Detail = "dotnet test", ExitCode = 0 });
            SettleInspectionActivity(ui.Renderer, 2);
            await ui.OnUi(() =>
            {
                Assert.False(ui.View.IsExpanded);
                Assert.Empty(ui.View.CollapsedRows);
                var failure = Assert.Single(ui.View.Lines, row => row.Style == TranscriptStyle.Tool && row.Text.Contains("locked.cs"));
                Assert.Contains(failure.Spans!, span => span.Attr.Foreground == TgPalette.ToColor(TuiToken.Err));
                Assert.Contains(ui.View.Lines, row => row.Text.Contains("permission", StringComparison.Ordinal));
                Assert.DoesNotContain(ui.View.Lines, row => row.Text.Contains("Tool     ", StringComparison.Ordinal));
            });
        });

    [Fact]
    public Task Late_activity_completion_refreshes_the_row_without_an_inspector() =>
        RunWorker(async ui =>
        {
            ui.Renderer.BeginTurn();
            var activity = InspectionActivity("edit", AgentActivityKind.Edit, "Edit") with
            { State = AgentActivityState.Started, FileName = "src/Session.cs" };
            ui.Renderer.Transcript.ApplyActivity(activity);
            SettleInspectionActivity(ui.Renderer, 1);
            await ui.OnUi(() => Assert.False(ui.View.IsExpanded));
            ui.Renderer.Transcript.ApplyActivity(activity with
            { State = AgentActivityState.Completed, Added = 12, Removed = 4, SafeCode = "succeeded" });
            await ui.OnUi(() =>
            {
                var row = Assert.Single(ui.View.Lines, line => line.Style == TranscriptStyle.Tool);
                Assert.Contains("Edited", row.Text);
                Assert.Contains("+12", row.Text);
                Assert.Contains(row.Spans!, span => span.Attr.Foreground == TgPalette.ToColor(TuiToken.Ok));
                Assert.Empty(ui.View.CollapsedRows);
                Assert.True(ui.View.RetainedRowCount <= TgTranscriptView.MaxRetainedRows);
            });
        });

    [Fact]
    public Task A_late_result_on_a_narrow_row_keeps_text_and_semantic_spans_in_sync() =>
        RunWorker(async ui =>
        {
            ui.Renderer.Width = 120;
            ui.Renderer.BeginTurn();
            var activity = InspectionActivity("command", AgentActivityKind.Terminal, "Terminal") with
            { State = AgentActivityState.Started, Detail = "dotnet test" };
            ui.Renderer.Transcript.ApplyActivity(activity);
            SettleInspectionActivity(ui.Renderer, 1);
            ui.Renderer.Width = 40;
            ui.Renderer.Transcript.ApplyActivity(activity with
            { State = AgentActivityState.Failed, ExitCode = 1, SafeCode = "process_failed" });
            await ui.OnUi(() =>
            {
                var row = Assert.Single(ui.View.Lines, line => line.Style == TranscriptStyle.Tool);
                Assert.True(TuiText.VisibleWidth(row.Text) <= 40);
                Assert.Equal(row.Text, string.Concat(row.Spans!.Select(span => span.Text)));
                Assert.Contains("exit 1", row.Text);
                Assert.Empty(ui.View.CollapsedRows);
            });
        });

    private static AgentActivityModel InspectionActivity(string id, AgentActivityKind kind, string display) =>
        new(id, kind, AgentActivityState.Completed, null, null, null, null, null, null, null, null,
            "succeeded", display, null, null);

    private static void SettleInspectionActivity(TuiSessionRenderer renderer, int sequence) =>
        renderer.Transcript.ApplyDurable(new(1, "evt_" + sequence, "run_1", "session_1", sequence,
            "tool.execution_completed", "2026-11-02T00:00:00Z", "running", "read_only"));
}
