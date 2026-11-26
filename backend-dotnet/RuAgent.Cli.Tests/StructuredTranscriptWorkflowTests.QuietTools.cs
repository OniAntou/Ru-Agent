using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class StructuredTranscriptWorkflowTests
{
    /// <summary>
    /// The native screen keeps what a turn did: each successful tool stays as one compact
    /// row (no result line, no diff), so a turn is not reduced to its thinking rows.
    /// </summary>
    [Theory]
    [InlineData(AgentActivityKind.Explore, "Read")]
    [InlineData(AgentActivityKind.Edit, "Edit")]
    [InlineData(AgentActivityKind.Terminal, "Terminal")]
    [InlineData(AgentActivityKind.Web, "Fetch")]
    [InlineData(AgentActivityKind.Mcp, "Mcp")]
    public Task Successful_tools_keep_one_compact_row_and_accurate_footer(AgentActivityKind kind, string display) =>
        RunWorker(async ui =>
        {
            ui.Renderer.BeginTurn();
            var activity = InspectionActivity("ok", kind, display) with { FileName = "answer.cs", ExitCode = 0 };
            ui.Renderer.Transcript.ApplyActivity(activity with { State = AgentActivityState.Started });
            Assert.Equal(TuiRunState.Tool, ui.Renderer.Status.State);
            Assert.Equal(0, ui.Renderer.Transcript.ToolsSettled);
            ui.Renderer.Transcript.ApplyActivity(activity);
            SettleInspectionActivity(ui.Renderer, 1);
            Assert.Equal(TuiRunState.WaitingModel, ui.Renderer.Status.State);
            ui.Renderer.Transcript.AppendAssistant("Verified answer.\n");
            ui.Renderer.Transcript.EndTurn(TuiTurnOutcome.Completed, null);
            await ui.OnUi(() =>
            {
                Assert.Single(ui.View.Lines, row => row.Style == TranscriptStyle.Tool);
                Assert.Contains(ui.View.Lines, row => row.Text == "Verified answer.");
                Assert.Contains(ui.View.Lines, row => row.Text.Contains("1 tool", StringComparison.Ordinal));
                if (kind == AgentActivityKind.Edit)
                    Assert.Contains(ui.View.Lines, row => row.Text.Contains("1 edit", StringComparison.Ordinal));
            });
        });

    [Fact]
    public Task Late_failure_after_a_compact_success_is_visible_and_counted_once() =>
        RunWorker(async ui =>
        {
            ui.Renderer.BeginTurn();
            var activity = InspectionActivity("corrected", AgentActivityKind.Terminal, "Terminal") with { Detail = "dotnet test" };
            ui.Renderer.Transcript.ApplyActivity(activity);
            SettleInspectionActivity(ui.Renderer, 1);
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
    public Task Replay_keeps_successes_compact_beside_failures_answer_and_counts() =>
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
                var rows = ui.View.Lines.Where(row => row.Style == TranscriptStyle.Tool).ToArray();
                Assert.Equal(2, rows.Length);
                Assert.Contains("success.cs", rows[0].Text);
                Assert.Contains("failure.cs", rows[1].Text);
                Assert.Contains(ui.View.Lines, row => row.Text == "Saved answer.");
                Assert.Contains(ui.View.Lines, row => row.Text.Contains("2 tools", StringComparison.Ordinal)
                    && row.Text.Contains("1 failed", StringComparison.Ordinal));
            });
        });

    [Fact]
    public Task A_successful_diff_stays_out_of_the_native_row_but_plain_logs_keep_it() =>
        RunWorker(async ui =>
        {
            const string diff = "--- before.cs\n+++ after.cs\n@@ -1 +1 @@\n-old\n+new";
            var activity = InspectionActivity("edit", AgentActivityKind.Edit, "Edit") with { FileName = "target.cs" };
            var result = new HarnessSafeEventEnvelope(1, "result", "run", "session", 1,
                "tool.execution_completed", "", "running", diff);
            ui.Renderer.BeginTurn();
            ui.Renderer.Transcript.ApplyActivity(activity);
            ui.Renderer.Transcript.ApplyDurable(result);
            await ui.OnUi(() =>
            {
                var row = Assert.Single(ui.View.Lines, row => row.Style == TranscriptStyle.Tool);
                Assert.Contains("target.cs", row.Text);
                Assert.DoesNotContain(ui.View.Lines, line => line.Text.Contains("+new", StringComparison.Ordinal));
            });

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
}
