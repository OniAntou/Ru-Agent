using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

/// <summary>
/// Tools that used to fall to a bare status row name their action, and searches name
/// their scope, without ever carrying typed text, script source or a URL's path.
/// </summary>
public sealed partial class AgentActivityProjectorTests
{
    [Theory]
    [InlineData("{\"action\":\"open\",\"url\":\"https://user:pw@github.com/org/repo?token=secret\"}", "open github.com")]
    [InlineData("{\"action\":\"navigate\",\"url\":\"file:///C:/private\"}", "navigate")]
    [InlineData("{\"action\":\"click\",\"ref\":\"e12\"}", "click e12")]
    [InlineData("{\"action\":\"click\",\"ref\":\"e12; rm -rf\"}", "click")]
    [InlineData("{\"action\":\"type\",\"ref\":\"e3\",\"text\":\"hunter2 password\"}", "type 16 characters")]
    [InlineData("{\"action\":\"type\",\"ref\":\"e3\"}", "type")]
    [InlineData("{\"action\":\"press\",\"key\":\"Enter\"}", "press Enter")]
    [InlineData("{\"action\":\"eval\",\"expression\":\"document.cookie\"}", "eval")]
    [InlineData("{\"action\":\"batch\",\"steps\":[{\"action\":\"click\"},{\"action\":\"press\"}]}", "batch 2 steps")]
    public void A_browser_row_names_its_action_and_a_safe_target(string arguments, string expected)
    {
        var activity = AgentActivityProjector.ProjectTool(Proposal("browser", arguments), AgentActivityState.Completed);

        Assert.Equal(AgentActivityKind.Web, activity.Kind);
        Assert.Equal("Browser", activity.ToolDisplay);
        Assert.Equal(expected, activity.Detail);
    }

    [Fact]
    public void A_question_and_a_subagent_wait_name_themselves()
    {
        var question = AgentActivityProjector.ProjectTool(
            Proposal("ask_user", "{\"question\":\"Which branch?\"}"), AgentActivityState.Waiting);
        Assert.Equal(AgentActivityKind.Status, question.Kind);
        Assert.Equal("Question", question.ToolDisplay);
        // The question itself is shown by the question panel, not copied into the row.
        Assert.Null(question.Detail);

        var wait = AgentActivityProjector.ProjectTool(
            Proposal("agent_wait", "{\"agent_ids\":[\"agent_one\",\"agent_two\"]}"), AgentActivityState.Started);
        Assert.Equal(2, wait.Count);
    }

    [Theory]
    [InlineData("grep", "{\"query\":\"steer\",\"path\":\"gui-winui3/Views/\"}", "gui-winui3/Views")]
    [InlineData("grep", "{\"query\":\"steer\",\"path\":\".\"}", null)]
    [InlineData("grep", "{\"query\":\"steer\",\"path\":\"C:/Users/someone\"}", null)]
    [InlineData("grep", "{\"query\":\"steer\",\"path\":\"../outside\"}", null)]
    [InlineData("glob", "{\"pattern\":\"**/*.cs\"}", "**/*.cs")]
    [InlineData("glob", "{\"pattern\":\"src\\\\*.xaml\"}", "src/*.xaml")]
    [InlineData("glob", "{\"pattern\":\"$(whoami)\"}", null)]
    public void A_search_row_names_its_scope_or_pattern_only_when_it_is_plain(string toolId, string arguments, string? expected)
    {
        var activity = AgentActivityProjector.ProjectTool(Proposal(toolId, arguments), AgentActivityState.Completed);
        Assert.Equal(expected, activity.Detail);
    }
}
