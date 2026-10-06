using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class AgentActionRouterTests
{
    [Theory]
    [InlineData("/help", AgentCommandKind.Help)]
    [InlineData("/skills", AgentCommandKind.Skills)]
    [InlineData("  /compact aggressive", AgentCommandKind.Compact)]
    [InlineData("/tasks", AgentCommandKind.Tasks)]
    public void Parser_returns_typed_commands(string text, AgentCommandKind expected)
    {
        var result = AgentActionRouter.Parse(text);

        Assert.True(result.Success);
        Assert.Equal(expected, result.Command!.Kind);
    }

    [Fact]
    public void Parser_parses_task_kill_without_retaining_command_text()
    {
        var result = AgentActionRouter.Parse("/tasks kill req-background-1");

        Assert.True(result.Success);
        Assert.Equal(AgentCommandKind.Tasks, result.Command!.Kind);
        Assert.Equal("req-background-1", result.Command.TaskKillId);
        Assert.DoesNotContain("req-background-1", result.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_parses_skill_name_without_retaining_command_text()
    {
        var result = AgentActionRouter.Parse("/skill pdf.analyzer");

        Assert.True(result.Success);
        Assert.Equal("pdf.analyzer", result.Command!.SkillId);
        Assert.DoesNotContain("pdf.analyzer", result.SafeMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/compact")]
    [InlineData("/compact invalid")]
    [InlineData("/help now")]
    [InlineData("/skill")]
    [InlineData("/skill bad-id")]
    [InlineData("/tasks kill")]
    [InlineData("/tasks stop req-1")]
    public void Parser_rejects_malformed_commands_without_an_action(string text)
    {
        var result = AgentActionRouter.Parse(text);

        Assert.False(result.Success);
        Assert.Equal("command_invalid", result.Code);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Parser_marks_unknown_slash_commands_separately_and_leaves_normal_text_alone()
    {
        var unknown = AgentActionRouter.Parse("/deploy now");
        Assert.Equal("command_unknown", unknown.Code);
        Assert.True(unknown.IsCommand);

        var ordinary = AgentActionRouter.Parse("hãy đọc file này");
        Assert.False(ordinary.IsCommand);
        Assert.Null(ordinary.Code);
        Assert.Null(ordinary.Command);
    }

    [Fact]
    public void Parser_rejects_oversized_command()
    {
        var oversized = AgentActionRouter.Parse("/help " + new string('x', AgentActionRouter.MaxInputBytes));
        Assert.Equal("command_invalid", oversized.Code);
    }
}
