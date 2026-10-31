using System.Text.Json.Nodes;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class TerminalWorkflowIntentTests
{
    [Theory]
    [InlineData("{\"purpose\":\"expected_failure\",\"expected_exit_code\":7}", true)]
    [InlineData("{\"purpose\":\"expected_failure\"}", false)]
    [InlineData("{\"purpose\":\"expected_failure\",\"expected_exit_code\":0}", false)]
    [InlineData("{\"purpose\":\"verification\"}", true)]
    [InlineData("{}", true)]
    public void Expected_exit_is_required_and_nonzero_for_expected_failure(string json, bool valid) =>
        Assert.Equal(valid, TerminalWorkflowIntent.HasValidExpectedExitCode(JsonNode.Parse(json)!.AsObject()));

    [Fact]
    public void Supersedes_stays_a_bounded_optional_field_on_both_launch_tools()
    {
        foreach (var tool in new[] { "run_process", "powershell" })
        {
            var schema = AgentToolDefinitionCatalog.Find(tool, "1.0.0")!.InputSchema;
            var property = schema["properties"]!["supersedes"]!;
            Assert.Equal(1, property["minItems"]!.GetValue<int>());
            Assert.Equal(TerminalWorkflowIntent.MaximumSupersededChecks, property["maxItems"]!.GetValue<int>());
            Assert.Equal(TerminalWorkflowIntent.MaximumSupersededCallIdChars, property["items"]!["maxLength"]!.GetValue<int>());
            Assert.DoesNotContain("supersedes", schema["required"]!.AsArray().Select(value => value!.GetValue<string>()));
            Assert.Contains("accepted but ignored", property["description"]!.GetValue<string>(), StringComparison.Ordinal);
        }
    }
}
