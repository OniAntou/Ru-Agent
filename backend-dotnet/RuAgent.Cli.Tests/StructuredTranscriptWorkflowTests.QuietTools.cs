using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class StructuredTranscriptWorkflowTests
{
    [Theory]
    [InlineData(AgentActivityKind.Explore, "Read")]
    [InlineData(AgentActivityKind.Edit, "Edit")]
    [InlineData(AgentActivityKind.Terminal, "Terminal")]
    [InlineData(AgentActivityKind.Web, "Fetch")]
    [InlineData(AgentActivityKind.Mcp, "Mcp")]
    public Task Successful_tools_leave_only_answer_and_accurate_footer(AgentActivityKind kind, string display) =>
        RunWorker(async ui =>
        {
            ui.Renderer.BeginTurn();
            TgTranscriptRow[] before = [];
            await ui.OnUi(() => before = ui.View.Lines.ToArray());
            var activity = InspectionActivity("ok", kind, display) with { FileName = "answer.cs", ExitCode = 0 };
            ui.Renderer.Transcript.ApplyActivity(activity with { State = AgentActivityState.Started });
            Assert.Equal(TuiRunState.Tool, ui.Renderer.Status.State);
            Assert.Equal(0, ui.Renderer.Transcript.ToolsSettled);
            ui.Renderer.Transcript.ApplyActivity(activity);
            SettleInspectionActivity(ui.Renderer, 1);
            await ui.OnUi(() => Assert.Equal(before, ui.View.Lines));
            Assert.Equal(TuiRunState.WaitingModel, ui.Renderer.Status.State);
            ui.Renderer.Transcript.AppendAssistant("Verified answer.\n");
            ui.Renderer.Transcript.EndTurn(TuiTurnOutcome.Completed, null);
            await ui.OnUi(() =>
            {
                Assert.DoesNotContain(ui.View.Lines, row => row.Style == TranscriptStyle.Tool);
                Assert.Contains(ui.View.Lines, row => row.Text == "Verified answer.");
                Assert.Contains(ui.View.Lines, row => row.Text.Contains("1 tool", StringComparison.Ordinal));
                if (kind == AgentActivityKind.Edit)
                    Assert.Contains(ui.View.Lines, row => row.Text.Contains("1 edit", StringComparison.Ordinal));
            });
        });

    [Fact]
    public Task Late_failure_after_hidden_success_is_visible_and_counted_once() =>
        RunWorker(async ui =>
        {
            ui.Renderer.BeginTurn();
            TgTranscriptRow[] before = [];
            await ui.OnUi(() => before = ui.View.Lines.ToArray());
            var activity = InspectionActivity("corrected", AgentActivityKind.Terminal, "Terminal") with { Detail = "dotnet test" };
            ui.Renderer.Transcript.ApplyActivity(activity);
            SettleInspectionActivity(ui.Renderer, 1);
            await ui.OnUi(() => Assert.Equal(before, ui.View.Lines));
            var failed = activity with { State = AgentActivityState.Failed, ExitCode = 1, SafeCode = "process_failed" };
            ui.Renderer.Transcript.ApplyActivity(failed);
            ui.Renderer.Transcript.ApplyActivity(failed);
            Assert.Equal(1, ui.Renderer.Transcript.ToolsSettled);
            ui.Renderer.Transcript.EndTurn(TuiTurnOutcome.Completed, null);
            await ui.OnUi(() =>
            {
                var row = Assert.Single(ui.View.Lines, row => row.Style == TranscriptStyle.Tool);
                Assert.Contains("dotnet test", row.Text);
                Assert.Contains("exit 1", row.Text);
                Assert.Contains(ui.View.Lines, row => row.Text.Contains("1 failed", StringComparison.Ordinal));
                Assert.Contains(ui.View.Lines, row => row.Text.Contains("1 tool", StringComparison.Ordinal));
            });
        });

    [Theory]
    [InlineData(AgentActivityState.Failed, 0)]
    [InlineData(AgentActivityState.Completed, 2)]
    public Task Failure_evidence_overrides_legacy_success_status(AgentActivityState state, int exitCode) =>
        RunWorker(async ui =>
        {
            ui.Renderer.BeginTurn();
            ui.Renderer.Transcript.ApplyActivity(InspectionActivity("error", AgentActivityKind.Terminal, "Terminal") with
            { State = state, Detail = "check", ExitCode = exitCode, SafeCode = "verification_failed" });
            ui.Renderer.Transcript.ApplyDurable(new(1, "result", "run", "session", 1,
                "tool.execution_completed", "", "succeeded", "verification_failed"));
            ui.Renderer.Transcript.ApplyDurable(new(1, "approval", "run", "session", 2,
                "approval.requested", "", "waiting", "workspace_write"));
            await ui.OnUi(() =>
            {
                var row = Assert.Single(ui.View.Lines, row => row.Style == TranscriptStyle.Tool);
                Assert.Contains(row.Spans!, span => span.Attr.Foreground == TgPalette.ToColor(TuiToken.Err));
                Assert.Contains(ui.View.Lines, row => row.Text.Contains("verification failed", StringComparison.Ordinal));
                Assert.Contains(ui.View.Lines, row => row.Text.Contains("approval needed", StringComparison.Ordinal));
            });
        });

    [Theory]
    [InlineData(AgentActivityState.Failed, 0)]
    [InlineData(AgentActivityState.Completed, 2)]
    public Task Late_generic_success_does_not_erase_reported_failure(AgentActivityState state, int exitCode) =>
        RunWorker(async ui =>
        {
            ui.Renderer.BeginTurn();
            var activity = InspectionActivity("error", AgentActivityKind.Terminal, "Terminal") with
            { State = state, Detail = "check", ExitCode = exitCode, SafeCode = "verification_failed" };
            ui.Renderer.Transcript.ApplyActivity(activity);
            SettleInspectionActivity(ui.Renderer, 1);
            ui.Renderer.Transcript.ApplyActivity(activity with
            { State = AgentActivityState.Completed, ExitCode = 0, SafeCode = "succeeded" });
            ui.Renderer.Transcript.EndTurn(TuiTurnOutcome.Completed, null);
            await ui.OnUi(() =>
            {
                var row = Assert.Single(ui.View.Lines, row => row.Style == TranscriptStyle.Tool);
                Assert.Contains(row.Spans!, span => span.Attr.Foreground == TgPalette.ToColor(TuiToken.Err));
                Assert.Contains(ui.View.Lines, row => row.Text.Contains("verification failed", StringComparison.Ordinal));
                Assert.Contains(ui.View.Lines, row => row.Text.Contains("1 failed", StringComparison.Ordinal));
            });
        });

    [Fact]
    public Task Replay_hides_successes_but_keeps_failures_answer_and_counts() =>
        RunWorker(async ui =>
        {
            ui.Renderer.BeginTurn();
            var success = InspectionActivity("read", AgentActivityKind.Explore, "Read") with { FileName = "success.cs" };
            var failure = InspectionActivity("edit", AgentActivityKind.Edit, "Edit") with
            { State = AgentActivityState.Failed, FileName = "failure.cs", SafeCode = "permission_denied" };
            ui.Renderer.Transcript.ReplayTurn(new AgentRunActivityReplay([success, failure], []),
                ["Saved answer."], null, null, TuiTurnOutcome.Completed, null);
            await ui.OnUi(() =>
            {
                var row = Assert.Single(ui.View.Lines, row => row.Style == TranscriptStyle.Tool);
                Assert.Contains("failure.cs", row.Text);
                Assert.DoesNotContain(ui.View.Lines, row => row.Text.Contains("success.cs", StringComparison.Ordinal));
                Assert.Contains(ui.View.Lines, row => row.Text == "Saved answer.");
                Assert.Contains(ui.View.Lines, row => row.Text.Contains("2 tools", StringComparison.Ordinal)
                    && row.Text.Contains("1 failed", StringComparison.Ordinal));
            });
        });

    [Fact]
    public Task Successful_diff_summary_adds_no_native_rows_but_plain_logs_keep_receipts() =>
        RunWorker(async ui =>
        {
            const string diff = "--- before.cs\n+++ after.cs\n@@ -1 +1 @@\n-old\n+new";
            var activity = InspectionActivity("edit", AgentActivityKind.Edit, "Edit") with { FileName = "target.cs" };
            var result = new HarnessSafeEventEnvelope(1, "result", "run", "session", 1,
                "tool.execution_completed", "", "running", diff);
            ui.Renderer.BeginTurn();
            TgTranscriptRow[] before = [];
            await ui.OnUi(() => before = ui.View.Lines.ToArray());
            ui.Renderer.Transcript.ApplyActivity(activity);
            ui.Renderer.Transcript.ApplyDurable(result);
            await ui.OnUi(() => Assert.Equal(before, ui.View.Lines));

            using var log = new StringWriter();
            var plain = new TuiSessionRenderer(log);
            plain.BeginTurn();
            plain.Transcript.ApplyActivity(activity);
            plain.Transcript.ApplyDurable(result);
            var text = TuiText.SanitizeUntrusted(log.ToString());
            Assert.Contains("target.cs", text);
            Assert.Contains("+new", text);
            Assert.Equal(1, plain.Transcript.ToolsSettled);
        });

    [Fact]
    public Task Removing_receipt_rebases_other_tools_turn_and_streaming_step_marks() =>
        RunWorker(async ui =>
        {
            ui.Writer.AppendActivityPresentation("gone", TranscriptLine.Plain("unknown", TranscriptRole.Tool));
            ui.Writer.BeginAssistantTurn();
            ui.Writer.AppendPresentation(TranscriptLine.Plain("keep"));
            ui.Writer.BeginAssistantStep("step");
            ui.Writer.CommitPresentation([TranscriptLine.Plain("superseded")]);
            ui.Writer.AppendActivityPresentation("kept", TranscriptLine.Plain("failed", TranscriptRole.Tool));
            ui.Writer.RemoveActivityPresentation("gone");
            ui.Writer.UpdateActivityPresentation("kept", TranscriptLine.Plain("updated failure", TranscriptRole.Tool));
            ui.Writer.RewindAssistantStep("step");
            await ui.OnUi(() => Assert.Equal(new[] { "keep", "updated failure" }, ui.View.Lines.Select(row => row.Text)));
        });

    [Fact]
    public Task Removing_receipt_preserves_turn_recovery_boundary() =>
        RunWorker(async ui =>
        {
            ui.Writer.AppendPresentation(TranscriptLine.Plain("earlier"));
            ui.Writer.AppendActivityPresentation("gone", TranscriptLine.Plain("unknown", TranscriptRole.Tool));
            ui.Writer.BeginAssistantTurn();
            ui.Writer.CommitPresentation([TranscriptLine.Plain("old answer")]);
            ui.Writer.RemoveActivityPresentation("gone");
            Assert.True(await ui.Writer.ReplaceAssistantPresentationAsync([TranscriptLine.Plain("recovered")]));
            await ui.OnUi(() => Assert.Equal(new[] { "earlier", "recovered" }, ui.View.Lines.Select(row => row.Text)));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Late_success_cannot_delete_rows_reused_after_clear_or_retention(bool retention) =>
        RunWorker(async ui =>
        {
            ui.Writer.AppendActivityPresentation("old", TranscriptLine.Plain("obsolete", TranscriptRole.Tool));
            if (retention)
                for (var index = 0; index <= TgTranscriptView.MaxRetainedRows; index++)
                    ui.Writer.AppendPresentation(TranscriptLine.Plain("retained " + index));
            else await ui.OnUi(() => ui.View.Clear());
            ui.Writer.AppendActivityPresentation("new", TranscriptLine.Plain("current failure", TranscriptRole.Tool));
            ui.Writer.RemoveActivityPresentation("old");
            ui.Writer.UpdateActivityPresentation("new", TranscriptLine.Plain("updated failure", TranscriptRole.Tool));
            await ui.OnUi(() =>
            {
                Assert.Equal("updated failure", ui.View.Lines[^1].Text);
                Assert.Single(ui.View.Lines, row => row.Style == TranscriptStyle.Tool);
            });
        });
}
