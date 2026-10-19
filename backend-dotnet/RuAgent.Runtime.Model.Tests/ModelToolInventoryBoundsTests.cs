using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Runtime.Model.Tests;

public sealed class ModelToolInventoryBoundsTests
{
    [Fact]
    public void Entire_canonical_inventory_reaches_generation_while_an_extra_definition_is_rejected()
    {
        var tools = new JsonArray(AgentToolDefinitionCatalog.Get().Select(d => (JsonNode)new JsonObject
            { ["tool_id"] = d.ToolId, ["tool_version"] = d.ToolVersion, ["input_schema"] = d.InputSchema }).ToArray());
        var accepted = new ModelGenerationSettings(tools);
        Assert.Equal(SystemPromptContext.MaxCapabilities, accepted.ModelTools!.Count);
        tools.Add(new JsonObject { ["tool_id"] = "unexpected_extra_tool" });
        Assert.Throws<ArgumentException>(() => new ModelGenerationSettings(tools));
    }
}
