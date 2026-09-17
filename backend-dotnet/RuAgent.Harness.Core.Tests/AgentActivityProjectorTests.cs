using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class AgentActivityProjectorTests
{
    private static readonly RunId Run = new("run_11111111111111111111111111111111");
    private static readonly ModelStepId Step = new("step_22222222222222222222222222222222");

    [Fact]
    public void Model_step_projection_exposes_duration_but_never_reasoning()
    {
        var activity = AgentActivityProjector.ProjectModelStep(
            Step,
            AgentActivityState.Completed,
            durationMs: 5000);

        Assert.Equal("act_22222222222222222222222222222222", activity.ActivityId);
        Assert.Equal(AgentActivityKind.Thinking, activity.Kind);
        Assert.Equal(AgentActivityState.Completed, activity.State);
        Assert.Equal(5000, activity.DurationMs);
        Assert.Null(activity.FileName);
        Assert.Null(activity.Symbol);
        Assert.DoesNotContain("reason", activity.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("read", "{\"op\":\"list\",\"path\":\"D:\\\\LLM\"}", "LLM")]
    [InlineData("glob", "{\"pattern\":\"**/*.cs\",\"path\":\"D:\\\\LLM\"}", "LLM")]
    public void Discovery_tools_project_folder_leaf_and_count(string toolId, string argumentsJson, string expectedLeaf)
    {
        var proposal = Proposal(toolId, argumentsJson);
        var receipt = Receipt("{\"entries\":[{},{}],\"count\":2}");

        var activity = AgentActivityProjector.ProjectTool(
            proposal,
            AgentActivityState.Completed,
            receipt);

        Assert.Equal(AgentActivityKind.Explore, activity.Kind);
        Assert.Equal(2, activity.Count);
        Assert.Equal(expectedLeaf, activity.FileName);
        Assert.Null(activity.Symbol);
        Assert.DoesNotContain("D:\\", activity.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_tool_without_usable_path_projects_no_leaf()
    {
        var proposal = Proposal("read", "{\"op\":\"list\",\"path\":\"D:\\\\\"}");

        var activity = AgentActivityProjector.ProjectTool(
            proposal,
            AgentActivityState.Started);

        Assert.Equal(AgentActivityKind.Explore, activity.Kind);
        Assert.Null(activity.FileName);
    }

    [Fact]
    public void Search_projection_keeps_safe_symbol_and_result_count()
    {
        var proposal = Proposal(
            "grep",
            "{\"query\":\"OnChatInputPreviewKeyDown\",\"path\":\"D:\\\\LLM\"}");
        var receipt = Receipt("{\"results\":[{\"path\":\"Views/ChatView.xaml.cs\",\"line\":150}],\"count\":1}");

        var activity = AgentActivityProjector.ProjectTool(
            proposal,
            AgentActivityState.Completed,
            receipt);

        Assert.Equal(AgentActivityKind.Search, activity.Kind);
        Assert.Equal("OnChatInputPreviewKeyDown", activity.Symbol);
        Assert.Equal(1, activity.Count);
        Assert.Null(activity.FileName);
        Assert.Null(activity.LineStart);
    }

    [Fact]
    public void Read_projection_uses_basename_and_validated_line_range()
    {
        var proposal = Proposal(
            "read",
            "{\"op\":\"read\",\"path\":\"D:\\\\LLM\\\\ChatView.xaml.cs\",\"line_start\":150,\"line_end\":200}");

        var activity = AgentActivityProjector.ProjectTool(
            proposal,
            AgentActivityState.Started);

        Assert.Equal(AgentActivityKind.Analyze, activity.Kind);
        Assert.Equal("ChatView.xaml.cs", activity.FileName);
        Assert.Equal(150, activity.LineStart);
        Assert.Equal(200, activity.LineEnd);
        Assert.DoesNotContain("D:\\", activity.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_projection_reports_only_bounded_line_counts()
    {
        var proposal = Proposal(
            "edit",
            "{\"path\":\"D:\\\\LLM\\\\ChatView.xaml\",\"old_string\":\"old\\ntext\",\"new_string\":\"new\\ntext\\nextra\",\"line_start\":10,\"line_end\":12}");

        var activity = AgentActivityProjector.ProjectTool(
            proposal,
            AgentActivityState.Started);

        Assert.Equal(AgentActivityKind.Edit, activity.Kind);
        Assert.Equal("ChatView.xaml", activity.FileName);
        Assert.Equal(10, activity.LineStart);
        Assert.Equal(12, activity.LineEnd);
        Assert.Equal(2, activity.Added);
        Assert.Equal(1, activity.Removed);
        Assert.Contains("@@ old\\ntext ==> new\\ntext\\nextra", activity.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("shell")]
    public void Terminal_projection_keeps_the_path_out_of_metadata(string mode)
    {
        var proposal = Proposal(
            "bash",
            "{\"mode\":\"" + mode + "\",\"command\":\"Get-ChildItem D:\\\\LLM\",\"cwd\":\"D:\\\\LLM\"}");

        var activity = AgentActivityProjector.ProjectTool(
            proposal,
            AgentActivityState.Started);

        Assert.Equal(AgentActivityKind.Terminal, activity.Kind);

        // The metadata fields are the ones the row renders as file/line/symbol, and a
        // terminal call has none of them: a path must not arrive through them.
        Assert.Null(activity.FileName);
        Assert.Null(activity.Symbol);
        Assert.Null(activity.LineStart);
        Assert.Null(activity.Count);

        // The command itself is the detail, and the working directory is not part of it.
        Assert.Equal("Get-ChildItem D:\\LLM", activity.Detail);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("shell")]
    public void A_running_terminal_carries_the_command_it_is_running(string mode)
    {
        // The command used to appear only while it waited for an approval decision, so a
        // command the user had approved went dark the moment it started running.
        var proposal = Proposal(
            "bash",
            "{\"mode\":\"" + mode + "\",\"script\":\"dotnet test --nologo\"}");

        var started = AgentActivityProjector.ProjectTool(proposal, AgentActivityState.Started);
        var waiting = AgentActivityProjector.ProjectTool(proposal, AgentActivityState.Waiting);

        Assert.Equal("dotnet test --nologo", started.Detail);
        Assert.Equal("dotnet test --nologo", waiting.Detail);
    }

    [Fact]
    public void A_settled_terminal_drops_the_command()
    {
        // Once the call is over the row reports its outcome, not the command it ran.
        var proposal = Proposal(
            "bash",
            "{\"mode\":\"run\",\"executable\":\"rm\",\"arguments\":[\"-rf\",\"node_modules\"]}");

        var completed = AgentActivityProjector.ProjectTool(proposal, AgentActivityState.Completed);

        Assert.Null(completed.Detail);
    }

    [Fact]
    public void A_started_non_terminal_tool_carries_no_command_detail()
    {
        // The widening applies to terminal calls only: a read or a search must not pick
        // up a detail it never had.
        var proposal = Proposal("read", "{\"op\":\"read\",\"path\":\"README.md\"}");

        var started = AgentActivityProjector.ProjectTool(proposal, AgentActivityState.Started);

        Assert.NotEqual(AgentActivityKind.Terminal, started.Kind);
        Assert.Null(started.Detail);
    }

    [Fact]
    public void Terminal_waiting_approval_projection_carries_command_detail_for_shell_and_run_modes()
    {
        var shellProposal = Proposal(
            "bash",
            "{\"mode\":\"shell\",\"script\":\"git reset --hard HEAD~1\"}");
        var shellActivity = AgentActivityProjector.ProjectTool(
            shellProposal,
            AgentActivityState.Waiting);

        Assert.Equal(AgentActivityKind.Terminal, shellActivity.Kind);
        Assert.Equal(AgentActivityState.Waiting, shellActivity.State);
        Assert.Equal("git reset --hard HEAD~1", shellActivity.Detail);

        var runProposal = Proposal(
            "bash",
            "{\"mode\":\"run\",\"executable\":\"rm\",\"arguments\":[\"-rf\",\"node_modules\"]}");
        var runActivity = AgentActivityProjector.ProjectTool(
            runProposal,
            AgentActivityState.Waiting);

        Assert.Equal(AgentActivityKind.Terminal, runActivity.Kind);
        Assert.Equal(AgentActivityState.Waiting, runActivity.State);
        Assert.Equal("rm -rf node_modules", runActivity.Detail);
    }

    [Theory]
    [InlineData("delete", "Delete")]
    [InlineData("restore", "Restore")]
    public void Delete_and_restore_project_basename_with_label(string op, string label)
    {
        var proposal = Proposal(
            "write",
            "{\"op\":\"" + op + "\",\"path\":\"D:\\\\LLM\\\\obsolete.txt\"}");

        var activity = AgentActivityProjector.ProjectTool(
            proposal,
            AgentActivityState.Started);

        Assert.Equal(AgentActivityKind.Edit, activity.Kind);
        Assert.Equal(label, activity.ToolDisplay);
        Assert.Equal("obsolete.txt", activity.FileName);
        Assert.DoesNotContain("D:\\", activity.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Model_step_projection_carries_bounded_detail()
    {
        var activity = AgentActivityProjector.ProjectModelStep(
            Step,
            AgentActivityState.Completed,
            durationMs: 4700,
            detail: "  Considering folder scope.  ");

        Assert.Equal("Considering folder scope.", activity.Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Model_step_projection_drops_blank_detail(string? detail)
    {
        var activity = AgentActivityProjector.ProjectModelStep(
            Step,
            AgentActivityState.Completed,
            detail: detail);

        Assert.Null(activity.Detail);
    }

    [Fact]
    public void Model_step_projection_clamps_oversized_detail()
    {
        var activity = AgentActivityProjector.ProjectModelStep(
            Step,
            AgentActivityState.Completed,
            detail: new string('x', 5_000));

        Assert.Equal(4_000, activity.Detail!.Length);
    }

    [Fact]
    public void Web_search_projects_query_symbol_without_url()
    {
        var proposal = Proposal(
            "web_search",
            "{\"query\":\"latest_release\"}");

        var activity = AgentActivityProjector.ProjectTool(
            proposal,
            AgentActivityState.Started);

        Assert.Equal(AgentActivityKind.Search, activity.Kind);
        Assert.Equal("Search", activity.ToolDisplay);
        Assert.Equal("latest_release", activity.Symbol);
    }

    [Fact]
    public void Web_fetch_stays_generic_without_url_metadata()
    {
        var proposal = Proposal(
            "web_fetch",
            "{\"url\":\"https://example.com/secret\"}");

        var activity = AgentActivityProjector.ProjectTool(
            proposal,
            AgentActivityState.Started);

        Assert.Equal(AgentActivityKind.Status, activity.Kind);
        Assert.Equal("Fetch", activity.ToolDisplay);
        Assert.Null(activity.Symbol);
        Assert.Null(activity.FileName);
        Assert.DoesNotContain("example.com", activity.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_tool_falls_back_to_generic_status()
    {
        var activity = AgentActivityProjector.ProjectTool(
            Proposal("unknown.tool", "{\"secret\":\"value\"}"),
            AgentActivityState.Failed,
            Receipt("{\"output\":\"private\"}", code: "denied"));

        Assert.Equal(AgentActivityKind.Status, activity.Kind);
        Assert.Equal(AgentActivityState.Failed, activity.State);
        Assert.Equal("denied", activity.SafeCode);
        Assert.Null(activity.FileName);
        Assert.Null(activity.Symbol);
    }

    [Fact]
    public void Invalid_metadata_is_omitted_instead_of_becoming_a_fallback_payload()
    {
        var proposal = Proposal(
            "read",
            "{\"op\":\"read\",\"path\":\"CON\",\"line_start\":200,\"line_end\":100}");

        var activity = AgentActivityProjector.ProjectTool(
            proposal,
            AgentActivityState.Completed);

        Assert.Equal(AgentActivityKind.Analyze, activity.Kind);
        Assert.Null(activity.FileName);
        Assert.Null(activity.LineStart);
        Assert.Null(activity.LineEnd);
        Assert.DoesNotContain("CON", activity.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Status_projection_carries_an_optional_bounded_reason()
    {
        var activity = AgentActivityProjector.ProjectStatus(
            "act_33333333333333333333333333333333",
            AgentActivityState.Waiting,
            "inspection_required",
            "Make one read-only inspection, or send Continue to override.");

        Assert.Equal(AgentActivityKind.Status, activity.Kind);
        Assert.Equal(AgentActivityState.Waiting, activity.State);
        Assert.Equal("inspection_required", activity.SafeCode);
        Assert.Contains("Continue", activity.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Publishing_to_a_failing_sink_does_not_throw_or_change_the_activity()
    {
        var activity = AgentActivityProjector.ProjectStatus("act_33333333333333333333333333333333", AgentActivityState.Completed);

        var exception = Record.Exception(() =>
            AgentActivityProjector.PublishSafe(new ThrowingSink(), Run, activity));

        Assert.Null(exception);
    }

    [Fact]
    public void Dropped_transient_frames_use_a_localized_status_code_and_bounded_count()
    {
        var activity = AgentActivityProjector.ProjectDropped(
            Step,
            toolFrames: false,
            droppedCount: 2_000_000);

        Assert.Equal(AgentActivityKind.Status, activity.Kind);
        Assert.Equal(AgentActivityState.Completed, activity.State);
        Assert.Equal("frames_dropped", activity.SafeCode);
        Assert.Equal(1_000_000, activity.Count);
        Assert.DoesNotContain("D:\\", activity.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectTool_Maps_Read_To_View_Label()
    {
        var proposal = Proposal("read", "{\"op\":\"read\",\"path\":\"D:\\\\LLM\\\\a.txt\"}");
        var activity = AgentActivityProjector.ProjectTool(proposal, AgentActivityState.Started);
        Assert.Equal(AgentActivityKind.Analyze, activity.Kind);
        Assert.Equal("View", activity.ToolDisplay);
    }

    [Fact]
    public void Edit_started_frame_carries_a_bounded_two_pane_preview_in_detail()
    {
        var proposal = Proposal(
            "edit",
            "{\"path\":\"notes.txt\",\"old_string\":\"line one\\nline two\",\"new_string\":\"line one\\nline TWO\"}");

        var started = AgentActivityProjector.ProjectTool(proposal, AgentActivityState.Started);

        Assert.Equal(AgentActivityKind.Edit, started.Kind);
        Assert.Equal("@@ line one\\nline two ==> line one\\nline TWO", started.Detail);

        var completed = AgentActivityProjector.ProjectTool(
            proposal,
            AgentActivityState.Completed,
            Receipt("{\"replacements\":1}"));
        Assert.Null(completed.Detail);
    }

    [Fact]
    public void Edit_preview_rejects_control_chars_and_stays_bounded()
    {
        var control = Proposal(
            "edit",
            "{\"path\":\"notes.txt\",\"old_string\":\"a\\u0001b\",\"new_string\":\"c\"}");
        Assert.Null(AgentActivityProjector.ProjectTool(control, AgentActivityState.Started).Detail);

        var big = Proposal(
            "edit",
            "{\"path\":\"notes.txt\",\"old_string\":\"" + new string('a', 5_000) + "\",\"new_string\":\"b\"}");
        var detail = AgentActivityProjector.ProjectTool(big, AgentActivityState.Started).Detail;
        Assert.NotNull(detail);
        Assert.True(detail.Length <= 4_000);
    }

    [Fact]
    public void Write_started_frame_carries_full_content_preview_in_detail()
    {
        var proposal = Proposal(
            "write",
            "{\"op\":\"write\",\"path\":\"script.js\",\"content\":\"line one\\nline two\"}");

        var started = AgentActivityProjector.ProjectTool(proposal, AgentActivityState.Started);

        Assert.Equal(AgentActivityKind.Edit, started.Kind);
        Assert.Equal("Write", started.ToolDisplay);
        Assert.Equal("@@  ==> line one\\nline two", started.Detail);

        var completed = AgentActivityProjector.ProjectTool(
            proposal,
            AgentActivityState.Completed,
            Receipt("{\"bytes\":42}"));
        Assert.Null(completed.Detail);
    }

    [Fact]
    public void Write_preview_skips_delete_restore_and_empty_content()
    {
        var deleted = Proposal("write", "{\"op\":\"delete\",\"path\":\"old.txt\"}");
        Assert.Null(AgentActivityProjector.ProjectTool(deleted, AgentActivityState.Started).Detail);

        var empty = Proposal("write", "{\"op\":\"write\",\"path\":\"new.txt\",\"content\":\"\"}");
        Assert.Null(AgentActivityProjector.ProjectTool(empty, AgentActivityState.Started).Detail);

        var big = Proposal(
            "write",
            "{\"op\":\"write\",\"path\":\"big.js\",\"content\":\"" + new string('a', 10_000) + "\"}");
        var detail = AgentActivityProjector.ProjectTool(big, AgentActivityState.Started).Detail;
        Assert.NotNull(detail);
        Assert.True(detail.Length <= 4_000);
    }

    [Fact]
    public void ProjectTool_Maps_Unknown_Tool_To_Null_Label()
    {
        var proposal = Proposal("unknown.tool", "{\"query\":\"x\"}");
        var activity = AgentActivityProjector.ProjectTool(proposal, AgentActivityState.Started);
        Assert.Equal(AgentActivityKind.Status, activity.Kind);
        Assert.Null(activity.ToolDisplay);
    }

    [Fact]
    public void Compaction_projection_keeps_only_bounded_counts_and_duration()
    {
        var activity = AgentActivityProjector.ProjectCompaction(
            new EventId("evt_66666666666666666666666666666666"),
            AgentActivityState.Completed,
            coveredEntries: 12,
            durationMs: 1200);

        Assert.Equal("act_66666666666666666666666666666666", activity.ActivityId);
        Assert.Equal(AgentActivityKind.Compaction, activity.Kind);
        Assert.Equal(AgentActivityState.Completed, activity.State);
        Assert.Equal(12, activity.Count);
        Assert.Equal(1200, activity.DurationMs);
        Assert.Null(activity.FileName);
        Assert.Null(activity.Symbol);
    }

    [Fact]
    public void Compaction_projection_clamps_out_of_range_values_and_keeps_the_error_code()
    {
        var activity = AgentActivityProjector.ProjectCompaction(
            new EventId("evt_66666666666666666666666666666666"),
            AgentActivityState.Failed,
            coveredEntries: -5,
            durationMs: 86_400_001,
            safeCode: "commit_conflict");

        Assert.Equal(AgentActivityKind.Compaction, activity.Kind);
        Assert.Equal(AgentActivityState.Failed, activity.State);
        Assert.Null(activity.Count);
        Assert.Null(activity.DurationMs);
        Assert.Equal("commit_conflict", activity.SafeCode);
    }

    [Fact]
    public void Retry_projection_reuses_bounded_tool_metadata_and_attempt_number()
    {
        var proposal = Proposal(
            "edit",
            "{\"path\":\"D:\\\\LLM\\\\ChatView.xaml\",\"old_string\":\"a\",\"new_string\":\"b\"}");

        var activity = AgentActivityProjector.ProjectRetry(proposal, attempt: 2);

        Assert.Equal("act_44444444444444444444444444444444_retry", activity.ActivityId);
        Assert.Equal(AgentActivityKind.Retry, activity.Kind);
        Assert.Equal(AgentActivityState.Completed, activity.State);
        Assert.Equal("ChatView.xaml", activity.FileName);
        Assert.Equal("Edit", activity.ToolDisplay);
        Assert.Equal(2, activity.Count);
        Assert.Equal("retrying", activity.SafeCode);
    }

    private static ToolProposal Proposal(string toolId, string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return new ToolProposal(
            Run,
            Step,
            new ToolCallId("call_44444444444444444444444444444444"),
            1,
            toolId,
            "1",
            document.RootElement);
    }

    private static ToolExecutionReceipt Receipt(string content, string code = "ok")
    {
        return new ToolExecutionReceipt(
            new ExecutionId("exec_55555555555555555555555555555555"),
            new ToolCallId("call_44444444444444444444444444444444"),
            ToolExecutionOutcome.Succeeded,
            code,
            null,
            sideEffectsPossible: false,
            modelContent: Encoding.UTF8.GetBytes(content),
            contentEncoding: Persistence.HarnessPayloadEncoding.Json);
    }

    private sealed class ThrowingSink : IAgentActivitySink
    {
        public void Publish(RunId runId, AgentActivity activity) => throw new InvalidOperationException("sink_down");
    }
}
