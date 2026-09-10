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
    [InlineData("read", "{\"op\":\"list\",\"path\":\"D:\\\\LLM\"}")]
    [InlineData("glob", "{\"pattern\":\"**/*.cs\",\"path\":\"D:\\\\LLM\"}")]
    public void Discovery_tools_project_count_only(string toolId, string argumentsJson)
    {
        var proposal = Proposal(toolId, argumentsJson);
        var receipt = Receipt("{\"entries\":[{},{}],\"count\":2}");

        var activity = AgentActivityProjector.ProjectTool(
            proposal,
            AgentActivityState.Completed,
            receipt);

        Assert.Equal(AgentActivityKind.Explore, activity.Kind);
        Assert.Equal(2, activity.Count);
        Assert.Null(activity.FileName);
        Assert.Null(activity.Symbol);
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
        Assert.DoesNotContain("old", activity.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("new", activity.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("shell")]
    public void Terminal_projection_is_generic_and_has_no_command_metadata(string mode)
    {
        var proposal = Proposal(
            "bash",
            "{\"mode\":\"" + mode + "\",\"command\":\"Get-ChildItem D:\\\\LLM\",\"cwd\":\"D:\\\\LLM\"}");

        var activity = AgentActivityProjector.ProjectTool(
            proposal,
            AgentActivityState.Started);

        Assert.Equal(AgentActivityKind.Terminal, activity.Kind);
        Assert.Null(activity.FileName);
        Assert.Null(activity.Symbol);
        Assert.DoesNotContain("Get-ChildItem", activity.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("D:\\", activity.ToString(), StringComparison.Ordinal);
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
    public void ProjectTool_Maps_Unknown_Tool_To_Null_Label()
    {
        var proposal = Proposal("unknown.tool", "{\"query\":\"x\"}");
        var activity = AgentActivityProjector.ProjectTool(proposal, AgentActivityState.Started);
        Assert.Equal(AgentActivityKind.Status, activity.Kind);
        Assert.Null(activity.ToolDisplay);
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
