using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class AgentActionRouterTests
{
    [Theory]
    [InlineData("/help", AgentCommandKind.Help)]
    [InlineData("/skills", AgentCommandKind.Skills)]
    [InlineData("  /compact aggressive", AgentCommandKind.Compact)]
    public void Parser_returns_typed_commands(string text, AgentCommandKind expected)
    {
        var result = AgentActionRouter.Parse(text);

        Assert.True(result.Success);
        Assert.Equal(expected, result.Command!.Kind);
    }

    [Fact]
    public void Parser_parses_bounded_skill_json_without_retaining_command_text()
    {
        var result = AgentActionRouter.Parse("/skill research.and_note {\"path\": \"src\"}");

        Assert.True(result.Success);
        Assert.Equal("research.and_note", result.Command!.SkillId);
        Assert.Equal("src", result.Command.JsonInput!["path"]!.GetValue<string>());
        Assert.DoesNotContain("research.and_note", result.SafeMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/compact")]
    [InlineData("/compact invalid")]
    [InlineData("/help now")]
    [InlineData("/skill bad-id {}")]
    [InlineData("/skill demo.skill []")]
    [InlineData("/skill demo.skill {bad}")]
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
    public void Parser_rejects_oversized_command_and_skill_json()
    {
        var oversized = AgentActionRouter.Parse("/help " + new string('x', AgentActionRouter.MaxInputBytes));
        Assert.Equal("command_invalid", oversized.Code);

        var oversizedJson = AgentActionRouter.Parse(
            "/skill demo.skill {\"value\":\"" + new string('x', AgentActionRouter.MaxSkillJsonBytes) + "\"}");
        Assert.Equal("command_invalid", oversizedJson.Code);
    }
}
