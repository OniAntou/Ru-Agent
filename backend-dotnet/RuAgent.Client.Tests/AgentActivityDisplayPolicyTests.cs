using RuAgent.Client;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class AgentActivityDisplayPolicyTests
{
    [Theory]
    [InlineData("<tool_call>\n{\"tool_id\":\"read\",\"tool_version\":\"1.0.0\",\"arguments\":{}}\n</tool_call>")]
    [InlineData("Xin chào!<tool_call>\n<(\"tool_id\":\"read\",\"tool_version\":\"1.0.0\",\"arguments\":{}}\n}>")]
    [InlineData("```json\n{\"tool_id\":\"read\",\"tool_version\":\"1.0.0\",\"arguments\":{}}\n```")]
    public void Tool_selection_envelopes_are_not_displayable(string text) =>
        Assert.True(AgentActivityDisplayPolicy.LooksLikeToolSelection(text));

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("To call a tool, write <tool_call> and then describe args.")]
    [InlineData("Discuss <tool_call>{} syntax here. Then keep explaining.")]
    [InlineData("A JSON object: {\"tool_id\":\"read\",\"arguments\":{}} is incomplete.")]
    public void Ordinary_text_and_incomplete_json_remain_displayable(string? text) =>
        Assert.False(AgentActivityDisplayPolicy.LooksLikeToolSelection(text));
}
