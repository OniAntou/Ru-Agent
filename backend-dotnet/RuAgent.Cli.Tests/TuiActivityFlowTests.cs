using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiActivityFlowTests
{
    [Fact]
    public void Read_result_uses_lines_and_has_no_hidden_inspector()
    {
        var (renderer, view) = Create();
        renderer.Transcript.ApplyActivity(Activity("read", AgentActivityKind.Explore, "Read") with
        { FileName = "src/Session.cs", LineStart = 24, LineEnd = 86, Count = 63 });
        Settle(renderer);

        Assert.Contains("63 lines", view.Activities["read"].Text);
        Assert.DoesNotContain("ctrl+o", view.Activities["read"].Text);
        Assert.Empty(view.Details["read"]);
        Assert.DoesNotContain("matches", view.Activities["read"].Text);
    }

    [Theory]
    [InlineData(0, AgentActivityState.Completed)]
    [InlineData(2, AgentActivityState.Failed)]
    public void Command_result_reports_actual_exit_code_inline_without_an_inspector(int exit, AgentActivityState state)
    {
        var (renderer, view) = Create();
        var command = "dotnet test " + new string('a', 180) + " --filter SessionTests";
        renderer.Transcript.ApplyActivity(Activity("command", AgentActivityKind.Terminal, "Terminal", state) with
        { Detail = command, ExitCode = exit, SafeCode = exit == 0 ? "succeeded" : "process_failed" });
        Settle(renderer);

        Assert.Contains($"exit {exit}", view.Activities["command"].Text);
        Assert.Empty(view.Details["command"]);
        if (state == AgentActivityState.Failed)
        {
            Assert.Contains("failed", view.Activities["command"].Text);
        }
    }

    [Fact]
    public void Completed_edits_use_action_verbs_but_failed_edits_do_not_claim_a_change()
    {
        var (renderer, view) = Create();
        renderer.Transcript.ApplyActivity(Activity("ok", AgentActivityKind.Edit, "Edit") with
        { FileName = "src/Session.cs", Added = 12, Removed = 4 });
        Settle(renderer);
        renderer.Transcript.ApplyActivity(Activity("bad", AgentActivityKind.Edit, "Edit", AgentActivityState.Failed) with
        { FileName = "locked.cs", SafeCode = "permission_denied" });
        Settle(renderer, 2);

        Assert.Contains("Edited", view.Activities["ok"].Text);
        Assert.Contains("+12", view.Activities["ok"].Text);
        Assert.DoesNotContain("Edited", view.Activities["bad"].Text);
        Assert.Contains("failed", view.Activities["bad"].Text);
        Assert.Contains(view.Rows, row => row.Text.Contains("permission", StringComparison.Ordinal));
        Assert.Empty(view.Details["bad"]);
    }

    [Fact]
    public void Late_result_updates_the_same_row_and_its_detail_without_duplicate_actions()
    {
        var (renderer, view) = Create();
        var started = Activity("command", AgentActivityKind.Terminal, "Terminal", AgentActivityState.Started) with
        { Detail = "dotnet test" };
        renderer.Transcript.ApplyActivity(started);
        Settle(renderer);
        Assert.Contains("outcome unavailable", view.Activities["command"].Text);
        renderer.Transcript.ApplyActivity(started with
        { State = AgentActivityState.Failed, ExitCode = 1, SafeCode = "process_failed", DurationMs = 1200 });

        Assert.Single(view.Activities);
        Assert.Equal(1, renderer.Transcript.ToolsSettled);
        Assert.Contains("exit 1", view.Activities["command"].Text);
        Assert.Empty(view.Details["command"]);
    }

    [Fact]
    public void A_closed_native_group_summarizes_the_work_before_the_next_answer()
    {
        var (renderer, view) = Create();
        foreach (var id in new[] { "first", "second" })
        {
            renderer.Transcript.ApplyActivity(Activity(id, AgentActivityKind.Explore, "Read") with { FileName = id + ".cs" });
            Settle(renderer);
        }
        renderer.Transcript.ApplyActivity(Activity("edit", AgentActivityKind.Edit, "Edit") with { FileName = "first.cs" });
        Settle(renderer);
        renderer.Transcript.AppendAssistant("The change is ready.\n");
        renderer.Flush();

        var summary = Assert.Single(view.Rows, row => row.Role == TranscriptRole.Meta && row.Text.Contains("2 read"));
        Assert.Contains("1 edited", summary.Text);
        Assert.True(view.Rows.IndexOf(summary) < view.Rows.FindIndex(row => row.Text.Contains("The change is ready.")));
    }

    [Fact]
    public void Native_live_phases_describe_observed_work_instead_of_inventing_reasoning()
    {
        var (renderer, _) = Create();
        Assert.Equal("Waiting for model", renderer.PresentActivity(100).Action);
        renderer.Transcript.ApplyActivity(Activity("think", AgentActivityKind.Thinking, null, AgentActivityState.Started));
        Assert.Equal("Thinking", renderer.PresentActivity(100).Action);
        renderer.Transcript.AppendAssistant("Here is the answer");
        Assert.Equal("Writing response", renderer.PresentActivity(100).Action);
    }

    [Theory]
    [InlineData(AgentActivityKind.Compaction, "Compacting context")]
    [InlineData(AgentActivityKind.Retry, "Retrying request")]
    public void Maintenance_work_keeps_its_own_phase(AgentActivityKind kind, string expected)
    {
        var (renderer, _) = Create();
        renderer.Transcript.ApplyActivity(Activity("maintenance", kind, null, AgentActivityState.Started));
        Assert.Equal(expected, renderer.PresentActivity(100).Action);
    }

    [Fact]
    public void Hidden_context_is_bounded_and_cannot_inject_terminal_controls()
    {
        var (renderer, view) = Create();
        renderer.Transcript.ApplyActivity(Activity("command", AgentActivityKind.Terminal, "Terminal") with
        { Detail = "dotnet\u001b[2J test\r\n" + new string('x', 10_000), ExitCode = 0 });
        Settle(renderer);
        Assert.Empty(view.Details["command"]);
        var row = view.Activities["command"].Text;
        Assert.DoesNotContain('\u001b', row);
        Assert.DoesNotContain('\r', row);
        Assert.DoesNotContain('\n', row);
        Assert.True(TuiText.VisibleWidth(row) <= 120);
    }

    [Fact]
    public void A_narrow_failed_command_prioritizes_exit_code_over_secondary_metrics()
    {
        var row = TuiToolRow.ComposePresentation(ToolLineStatus.Failed, "Run", "dotnet test",
            "exit 2 · 12 lines · 1.2s", 40);
        Assert.Contains("failed", row.Text);
        Assert.Contains("exit 2", row.Text);
        Assert.True(TuiText.VisibleWidth(row.Text) <= 40);
    }

    [Fact]
    public void A_count_only_late_completion_keeps_the_original_file_context()
    {
        var (renderer, view) = Create();
        renderer.Transcript.ApplyActivity(Activity("read", AgentActivityKind.Explore, "Read", AgentActivityState.Started) with
        { FileName = "src/Session.cs", LineStart = 10, LineEnd = 30 });
        Settle(renderer);
        renderer.Transcript.ApplyActivity(Activity("read", AgentActivityKind.Explore, "Read") with { Count = 21 });
        Assert.Contains("src/Session.cs:10-30", view.Activities["read"].Text);
        Assert.Contains("21 lines", view.Activities["read"].Text);
        Assert.Empty(view.Details["read"]);
    }

    [Fact]
    public void A_tool_retry_names_the_tool_work_instead_of_a_model_request()
    {
        var (renderer, _) = Create();
        renderer.Transcript.ApplyActivity(Activity("retry", AgentActivityKind.Retry, "Read", AgentActivityState.Started) with
        { FileName = "src/Session.cs", Count = 2 });
        var phase = renderer.PresentActivity(100);
        Assert.Equal("Retrying tool", phase.Action);
        Assert.Contains("src/Session.cs", phase.Target);
    }

    [Fact]
    public void A_later_tool_row_keeps_the_thinking_footer_hint()
    {
        var (renderer, view) = Create();
        renderer.Transcript.ApplyActivity(Activity("thinking", AgentActivityKind.Thinking, null) with { Detail = "Reported reasoning" });
        renderer.Transcript.ApplyActivity(Activity("read", AgentActivityKind.Explore, "Read") with { FileName = "src/Session.cs" });
        Settle(renderer);
        renderer.Transcript.AppendAssistant("Ready.\n");
        renderer.EndTurn(TuiTurnOutcome.Completed, null);
        Assert.Contains(view.Rows, row => row.Role == TranscriptRole.Meta && row.Text.Contains("ctrl+o thinking"));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void Common_actions_align_their_targets_and_do_not_wrap(int width)
    {
        var (renderer, view) = Create();
        renderer.Width = width;
        renderer.Transcript.ApplyActivity(Activity("read", AgentActivityKind.Explore, "Read") with { FileName = "first.cs" });
        Settle(renderer);
        renderer.Transcript.ApplyActivity(Activity("edit", AgentActivityKind.Edit, "Edit") with { FileName = "second.cs" });
        Settle(renderer, 2);
        Assert.Equal(view.Activities["read"].Text.IndexOf("first.cs", StringComparison.Ordinal),
            view.Activities["edit"].Text.IndexOf("second.cs", StringComparison.Ordinal));
        Assert.All(view.Activities.Values, row => Assert.True(TuiText.VisibleWidth(row.Text) <= width));
    }

    [Fact]
    public void A_live_action_and_its_finished_row_keep_the_target_in_the_same_column()
    {
        var (renderer, view) = Create();
        var activity = Activity("read", AgentActivityKind.Explore, "Read", AgentActivityState.Started) with { FileName = "src/Session.cs" };
        renderer.Transcript.ApplyActivity(activity);
        var live = string.Concat(renderer.PresentActivity(120).Fit(120).Select(span => span.Text));
        renderer.Transcript.ApplyActivity(activity with { State = AgentActivityState.Completed, SafeCode = "succeeded" });
        Settle(renderer);
        Assert.Equal(live.IndexOf("src/Session.cs", StringComparison.Ordinal),
            view.Activities["read"].Text.IndexOf("src/Session.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void A_long_diff_is_bounded_inline_and_keeps_its_tail()
    {
        var (renderer, view) = Create();
        renderer.Transcript.ApplyActivity(Activity("edit", AgentActivityKind.Edit, "Edit") with { FileName = "src/Session.cs" });
        renderer.Transcript.ApplyDurable(new(1, "evt_diff", "run_1", "sess_1", 1,
            "tool.execution_completed", "2026-11-02T00:00:00Z", "succeeded", "@@ -1 +1 @@\n-previous\n+" + new string('x', 1500) + "DIFF_TAIL"));
        Assert.Contains(view.Rows, row => row.Text.EndsWith("DIFF_TAIL", StringComparison.Ordinal));
        Assert.Empty(view.Details["edit"]);
    }

    [Fact]
    public void A_withdrawn_response_returns_to_waiting_for_the_next_model_step()
    {
        var (renderer, _) = Create();
        renderer.Transcript.AppendAssistant("Superseded reply");
        renderer.Transcript.DiscardAssistantTail();
        Assert.Equal("Waiting for model", renderer.PresentActivity(100).Action);
    }

    [Fact]
    public void A_retry_attempt_is_not_presented_as_a_result_count()
    {
        var (renderer, view) = Create();
        renderer.Transcript.ApplyActivity(Activity("retry", AgentActivityKind.Retry, "Terminal") with { Count = 2 });
        Assert.Null(renderer.PresentActivity(100).Target);
        Assert.Contains(view.Rows, row => row.Text.Contains("attempt 2"));
        Assert.DoesNotContain(view.Rows, row => row.Text.Contains("2 results"));
    }

    private static (TuiSessionRenderer, ActivityView) Create()
    {
        var view = new ActivityView();
        var renderer = new TuiSessionRenderer(view) { Width = 120 };
        renderer.Transcript.FullScreenCards = true;
        renderer.BeginTurn();
        return (renderer, view);
    }

    private static AgentActivityModel Activity(string id, AgentActivityKind kind, string? tool,
        AgentActivityState state = AgentActivityState.Completed) =>
        new(id, kind, state, null, null, null, null, null, null, null, null,
            state == AgentActivityState.Completed ? "succeeded" : "started", tool, null, null);

    private static void Settle(TuiSessionRenderer renderer, long sequence = 1) =>
        renderer.Transcript.ApplyDurable(new(1, "evt_" + sequence, "run_1", "sess_1", sequence,
            "tool.execution_completed", "2026-11-02T00:00:00Z", "running", "read_only"));

    private sealed class ActivityView : StringWriter, ITranscriptPresentationWriter
    {
        internal List<TranscriptLine> Rows { get; } = [];
        internal Dictionary<string, TranscriptLine> Activities { get; } = [];
        internal Dictionary<string, IReadOnlyList<string>> Details { get; } = [];
        public void PreviewPresentation(IReadOnlyList<TranscriptLine> rows) { }
        public void CommitPresentation(IReadOnlyList<TranscriptLine> rows) => Rows.AddRange(rows);
        public void AppendPresentation(TranscriptLine row) => Rows.Add(row);
        public void AppendPresentationChunk(TranscriptLine row) => Rows.Add(row);
        public void AppendActivityPresentation(string id, TranscriptLine row, IReadOnlyList<string>? details = null)
        { Activities[id] = row; Details[id] = details ?? []; Rows.Add(row); }
        public void UpdateActivityPresentation(string id, TranscriptLine row, IReadOnlyList<string>? details = null)
        { Activities[id] = row; if (details is not null) Details[id] = details; }
        public Task<bool> ReplaceAssistantPresentationAsync(IReadOnlyList<TranscriptLine> rows) => Task.FromResult(false);
    }
}
