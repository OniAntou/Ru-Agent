using System.Text.Json.Nodes;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class TerminalWorkflowIntentTests
{
    [Theory]
    [InlineData("{\"purpose\":\"verification\",\"supersedes\":[\"probe\"]}", true)]
    [InlineData("{\"purpose\":\"inspection\",\"supersedes\":[\"probe\"]}", false)]
    [InlineData("{\"supersedes\":[\"probe\"]}", false)]
    [InlineData("{\"purpose\":\"verification\",\"supersedes\":[]}", false)]
    [InlineData("{\"purpose\":\"verification\",\"supersedes\":[\"probe\",\"probe\"]}", false)]
    [InlineData("{\"purpose\":\"verification\",\"supersedes\":[\"\"]}", false)]
    [InlineData("{\"purpose\":\"verification\",\"supersedes\":null}", false)]
    [InlineData("{\"purpose\":\"verification\",\"supersedes\":[3]}", false)]
    [InlineData("{}", true)]
    public void Replacement_intent_is_explicit_and_well_formed(string json, bool valid) =>
        Assert.Equal(valid, TerminalWorkflowIntent.HasValidSupersedes(JsonNode.Parse(json)!.AsObject()));

    [Fact]
    public void Replacement_metadata_is_bounded_and_exposed_on_both_launch_tools()
    {
        var args = new JsonObject { ["purpose"] = "verification", ["supersedes"] = new JsonArray(new string('a', 257)) };
        Assert.False(TerminalWorkflowIntent.HasValidSupersedes(args));
        args["supersedes"] = new JsonArray(Enumerable.Range(0, 17).Select(i => (JsonNode?)JsonValue.Create("id-" + i)).ToArray());
        Assert.False(TerminalWorkflowIntent.HasValidSupersedes(args));
        foreach (var tool in new[] { "run_process", "powershell" })
        {
            var schema = AgentToolDefinitionCatalog.Find(tool, "1.0.0")!.InputSchema;
            Assert.Equal(16, schema["properties"]!["supersedes"]!["maxItems"]!.GetValue<int>());
            Assert.DoesNotContain("supersedes", schema["required"]!.AsArray().Select(value => value!.GetValue<string>()));
        }
    }
}
