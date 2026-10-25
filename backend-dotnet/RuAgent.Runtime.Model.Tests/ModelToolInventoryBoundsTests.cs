using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Runtime.Model.Tests;

public sealed class ModelToolInventoryBoundsTests
{
    [Fact]
    public void Entire_canonical_inventory_fits_and_generation_rejects_count_above_capacity()
    {
        var tools = new JsonArray(AgentToolDefinitionCatalog.Get().Select(d => (JsonNode)new JsonObject
            { ["tool_id"] = d.ToolId, ["tool_version"] = d.ToolVersion, ["input_schema"] = d.InputSchema }).ToArray());
        var accepted = new ModelGenerationSettings(tools);
        Assert.Equal(AgentToolDefinitionCatalog.Get().Count, accepted.ModelTools!.Count);
        Assert.InRange(accepted.ModelTools.Count, 1, SystemPromptContext.MaxCapabilities);
        while (tools.Count < SystemPromptContext.MaxCapabilities)
            tools.Add(new JsonObject { ["tool_id"] = "reserved_" + tools.Count });
        Assert.Equal(SystemPromptContext.MaxCapabilities, new ModelGenerationSettings(tools).ModelTools!.Count);
        tools.Add(new JsonObject { ["tool_id"] = "above_capacity" });
        Assert.Throws<ArgumentException>(() => new ModelGenerationSettings(tools));
    }
}
