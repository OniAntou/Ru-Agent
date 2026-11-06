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
    public void The_turn_footer_carries_the_work_tally_and_blocks_print_no_summary()
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
        renderer.Transcript.EndTurn(TuiTurnOutcome.Completed, null);

        // A block tally printed a second count right above the footer's own.
        Assert.DoesNotContain(view.Rows, row => row.Text.Contains("2 read"));
        var footer = Assert.Single(view.Rows, row => row.Text.Contains("3 tools"));
        Assert.Contains("1 edit", footer.Text);
        Assert.True(view.Rows.IndexOf(footer) > view.Rows.FindIndex(row => row.Text.Contains("The change is ready.")));
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

    [Fact]
    public void Status_rows_hide_ok_word_known_codes_and_never_hide_a_failure()
    {
        var (renderer, view) = Create();
        renderer.Transcript.ApplyActivity(Activity("ok", AgentActivityKind.Status, null) with { SafeCode = "ok" });
        renderer.Transcript.ApplyActivity(Activity("spawn", AgentActivityKind.Status, null) with { SafeCode = "subagent_spawned" });
        renderer.Transcript.ApplyActivity(Activity("odd", AgentActivityKind.Status, null, AgentActivityState.Failed) with { SafeCode = "ok" });
        renderer.Transcript.ApplyActivity(Activity("new", AgentActivityKind.Status, null) with { SafeCode = "brand_new_code" });

        var texts = view.Rows.Select(row => row.Text.Trim()).ToArray();
        Assert.DoesNotContain("· ok", texts);
        Assert.Contains("· started a subagent", texts);
        Assert.Contains("· status: ok", texts);
        Assert.Contains("· status: brand new code", texts);
    }

    [Fact]
    public void A_command_row_names_its_first_real_statement_and_counts_the_rest()
    {
        var (renderer, view) = Create();
        var script = "$ErrorActionPreference='Stop'; Set-Location D:\\work\n$env:PYTHONIOENCODING='utf-8'\n"
            + "python -m unittest discover -s tests; Write-Output 'done'";
        renderer.Transcript.ApplyActivity(Activity("command", AgentActivityKind.Terminal, "Terminal") with { Detail = script, ExitCode = 0 });
        Settle(renderer);

        var row = view.Activities["command"].Text;
        Assert.Contains("python -m unittest discover -s tests", row);
        Assert.Contains("(+1 more)", row);
        Assert.DoesNotContain("ErrorActionPreference", row);
        Assert.Equal(("Write-Output 'a; b'", 0), TuiActivityLabels.CommandHead("Write-Output 'a; b'"));
    }

    [Fact]
    public void A_nonzero_exit_reason_is_not_repeated_but_a_real_reason_is_kept()
    {
        var (renderer, view) = Create();
        renderer.Transcript.ApplyActivity(Activity("exit", AgentActivityKind.Terminal, "Terminal", AgentActivityState.Failed) with
        { Detail = "dotnet test", ExitCode = 1, SafeCode = "process_exit_nonzero" });
        Settle(renderer);
        renderer.Transcript.ApplyActivity(Activity("limit", AgentActivityKind.Terminal, "Terminal", AgentActivityState.Failed) with
        { Detail = "sleep 999", SafeCode = "timeout_exceeds_hard_max" });
        Settle(renderer, 2);

        Assert.Contains("exit 1", view.Activities["exit"].Text);
        Assert.DoesNotContain(view.Rows, row => row.Text.Contains("process exit nonzero"));
        Assert.Contains(view.Rows, row => row.Text.Contains("timeout exceeds hard max"));
    }

    [Fact]
    public void A_thought_leaves_one_row_with_its_duration_and_a_blink_leaves_none()
    {
        var (renderer, view) = Create();
        renderer.Transcript.ApplyActivity(Activity("long", AgentActivityKind.Thinking, null) with { DurationMs = 31_000, Detail = "plan" });
        renderer.Transcript.ApplyActivity(Activity("short", AgentActivityKind.Thinking, null) with { DurationMs = 400 });

        Assert.Single(view.Rows, row => row.Text.Contains("Thought"));
        Assert.Contains(view.Rows, row => row.Text.Contains("◦ Thought 31.0s"));
    }

    [Fact]
    public void A_thought_row_opens_before_the_answer_its_step_streams()
    {
        // The Host closes a step only after the answer text is already out, so a row
        // announced on completion lands under the reply it introduced. Opening it when
        // the step starts keeps the feed in the order the work happened.
        var (renderer, view) = Create();
        renderer.Transcript.ApplyActivity(
            Activity("think", AgentActivityKind.Thinking, null, AgentActivityState.Started));
        renderer.Transcript.AppendAssistant("Here is the answer.\n\n");
        renderer.Transcript.ApplyActivity(
            Activity("think", AgentActivityKind.Thinking, null) with { DurationMs = 6_100 });
        renderer.Transcript.Flush();

        var opened = view.Rows.FindIndex(row => row.Text.Contains("Thinking", StringComparison.Ordinal));
        var answer = view.Rows.FindIndex(row => row.Text.Contains("Here is the answer", StringComparison.Ordinal));
        Assert.True(opened >= 0, "the live thinking row is missing");
        Assert.True(answer >= 0, "the answer row is missing");
        Assert.True(opened < answer, $"the thinking row at {opened} should precede the answer at {answer}");
        Assert.Contains("Thought 6.1s", view.Activities["think"].Text);
    }

    [Fact]
    public void A_short_thought_settles_its_open_row_without_adding_a_second_one()
    {
        var (renderer, view) = Create();
        renderer.Transcript.ApplyActivity(
            Activity("brief", AgentActivityKind.Thinking, null, AgentActivityState.Started));
        renderer.Transcript.ApplyActivity(
            Activity("brief", AgentActivityKind.Thinking, null) with { DurationMs = 400 });

        Assert.Single(view.Rows, row => row.Text.Contains("Thinking") || row.Text.Contains("Thought"));
        Assert.Contains("Thought briefly", view.Activities["brief"].Text);
    }

    [Fact]
    public void An_attached_turn_counts_from_the_run_start()
    {
        var (renderer, _) = Create();
        var now = DateTimeOffset.UtcNow;
        renderer.Transcript.StartedAt(now.AddMinutes(-3), now);
        Assert.InRange(renderer.Transcript.ElapsedSeconds, 180, 200);
        renderer.BeginTurn();
        Assert.InRange(renderer.Transcript.ElapsedSeconds, 0, 5);
        renderer.Transcript.StartedAt(now.AddMinutes(5), now);
        Assert.InRange(renderer.Transcript.ElapsedSeconds, 0, 5);
    }

    [Fact]
    public void A_replayed_turn_draws_the_same_rows_as_the_live_turn()
    {
        var thought = Activity("think", AgentActivityKind.Thinking, null) with { DurationMs = 12_000, Detail = "plan" };
        var command = Activity("command", AgentActivityKind.Terminal, "Terminal") with { Detail = "dotnet test", ExitCode = 0, DurationMs = 900 };

        var (live, liveView) = Create();
        live.Transcript.ApplyActivity(thought);
        live.Transcript.AppendAssistant("Checking the tests first.\n\n");
        live.Transcript.ApplyActivity(command with { State = AgentActivityState.Started });
        live.Transcript.ApplyActivity(command);
        Settle(live);
        live.Transcript.AppendAssistant("All green.\n\n");
        live.Transcript.EndTurn(TuiTurnOutcome.Completed, null);

        var (replayed, replayView) = Create();
        var start = DateTimeOffset.Parse("2026-11-04T10:00:00Z");
        replayed.Transcript.ReplayTurn(new AgentRunActivityReplay([thought, command],
            [new(1, "activity", null, "think"), new(2, "text", "Checking the tests first.", null),
             new(3, "activity", null, "command"), new(4, "text", "All green.", null)]),
            ["All green."], start, start.AddSeconds(95), TuiTurnOutcome.Completed, null);

        static string[] Body(List<TranscriptLine> rows) => rows.Select(row => row.Text.TrimEnd())
            .Where(text => text.Length > 0 && !text.Contains("✓ ", StringComparison.Ordinal) || text.Contains("Ran", StringComparison.Ordinal)).ToArray();
        Assert.Equal(Body(liveView.Rows), Body(replayView.Rows));
        // The footer carries the run's real duration, and the reply is not drawn twice.
        Assert.Contains(replayView.Rows, row => row.Text.Contains("✓ 1m35s · 1 tool", StringComparison.Ordinal));
        Assert.Single(replayView.Rows, row => row.Text.Contains("All green.", StringComparison.Ordinal));
    }

    [Fact]
    public void Replayed_narration_joins_transport_chunks_and_keeps_a_missing_final_reply()
    {
        var command = Activity("command", AgentActivityKind.Terminal, "Terminal") with { Detail = "dotnet test", ExitCode = 0 };
        var (replayed, view) = Create();
        replayed.Transcript.ReplayTurn(new AgentRunActivityReplay([command],
            [new(1, "text", "Checking the ", null), new(2, "text", "tests first.", null), new(3, "activity", null, "command")]),
            ["All green."], null, null, TuiTurnOutcome.Completed, null);

        // Chunks are not paragraphs: the sentence stays whole.
        Assert.Contains(view.Rows, row => row.Text.Contains("Checking the tests first.", StringComparison.Ordinal));
        // The transcript stopped before the reply; the persisted reply still ends the turn.
        var reply = view.Rows.FindIndex(row => row.Text.Contains("All green.", StringComparison.Ordinal));
        Assert.True(reply > view.Rows.FindIndex(row => row.Text.Contains("dotnet test", StringComparison.Ordinal)));
    }

    [Fact]
    public void Reopened_messages_group_into_turns_by_their_run()
    {
        using var page = System.Text.Json.JsonDocument.Parse("""
            {"messages":[
              {"role":"user","content":"build it","source_run_id":"run_a"},
              {"role":"user","content":"continue","source_run_id":"run_b"},
              {"role":"ai","content":"done","source_run_id":"run_b"},
              {"role":"user","content":"thanks"},
              {"role":"ai","content":"you're welcome"}
            ]}
            """);
        var turns = ChatSessionTurnReplay.Group(page.RootElement);
        Assert.Equal(new string?[] { "run_a", "run_b", null }, turns.Select(turn => turn.RunId));
        Assert.Equal(["continue"], turns[1].Prompts.ToArray());
        Assert.Equal(["done"], turns[1].Replies.ToArray());
        Assert.Empty(turns[0].Replies);
        Assert.Equal(["you're welcome"], turns[2].Replies.ToArray());
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
