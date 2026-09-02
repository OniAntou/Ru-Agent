using System.Text.Json.Nodes;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class AgentToolDefinitionCatalogTests
{
    private static readonly string[] ExpectedOrder =
    [
        "files.list",
        "files.glob",
        "files.search",
        "files.read",
        "files.write",
        "files.edit",
        "files.delete",
        "files.restore",
        "terminal.run",
        "terminal.shell",
        "web.search",
        "web.fetch",
        "help.docs",
        "system.time",
        "system.status",
        "app.list",
        "notes.create",
        "system.volume",
    ];

    [Fact]
    public void Get_returns_exact_stable_inventory_and_exposure_split()
    {
        var definitions = AgentToolDefinitionCatalog.Get();

        Assert.Equal(ExpectedOrder, definitions.Select(item => item.ToolId));
        Assert.Equal(18, definitions.Count);
        Assert.Equal(
            13,
            definitions.Count(item => item.Exposure.HasFlag(AgentToolExposure.ModelVisible)));
        Assert.Equal(
            5,
            definitions.Count(item => !item.Exposure.HasFlag(AgentToolExposure.ModelVisible)));
        Assert.Equal(
            AgentToolExposure.BrokerInternal | AgentToolExposure.DeterministicHost,
            AgentToolDefinitionCatalog.Find("system.volume", "1.0.0")!.Exposure);
    }

    [Fact]
    public void Schema_and_example_access_return_fresh_nodes()
    {
        var definition = AgentToolDefinitionCatalog.Find("files.read", "1.0.0")!;

        var schema = definition.InputSchema;
        var example = definition.ExampleArguments!;
        schema["mutated"] = true;
        example["mutated"] = true;

        Assert.False(definition.InputSchema.ContainsKey("mutated"));
        Assert.False(definition.ExampleArguments!.ContainsKey("mutated"));
    }

    [Fact]
    public void Mutation_and_search_schemas_exclude_private_state_and_legacy_aliases()
    {
        foreach (var toolId in new[] { "files.write", "files.edit", "files.delete" })
        {
            var properties = Properties(toolId);
            Assert.False(properties.ContainsKey("expected_sha256"));
        }

        var search = Properties("files.search");
        Assert.True(search.ContainsKey("is_regex"));
        Assert.False(search.ContainsKey("regex"));

        Assert.Equal(["path"], Required("files.delete"));
        Assert.Equal(["recovery_id"], Required("files.restore"));

        var help = Properties("help.docs");
        Assert.Equal("string", help["query"]!["type"]!.GetValue<string>());
        Assert.Equal(256, help["query"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(1, help["max_results"]!["minimum"]!.GetValue<int>());
        Assert.Equal(5, help["max_results"]!["maximum"]!.GetValue<int>());
        Assert.False(help.ContainsKey("path"));
    }

    [Fact]
    public void Every_definition_has_valid_identity_description_scope_schema_and_example()
    {
        foreach (var definition in AgentToolDefinitionCatalog.Get())
        {
            Assert.Equal("1.0.0", definition.ToolVersion);
            Assert.Matches("^[a-z][a-z0-9_]*(?:\\.[a-z][a-z0-9_]*)+$", definition.ToolId);
            Assert.False(string.IsNullOrWhiteSpace(definition.Description));
            Assert.Contains(definition.Scope, AgentToolScopes.All);
            Assert.NotEqual(AgentToolExposure.None, definition.Exposure);

            var schema = definition.InputSchema;
            Assert.Equal("object", schema["type"]!.GetValue<string>());
            Assert.False(schema["additionalProperties"]!.GetValue<bool>());
            Assert.IsType<JsonObject>(schema["properties"]);

            var example = definition.ExampleArguments;
            if (example is null)
            {
                continue;
            }

            var allowed = schema["properties"]!.AsObject().Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            Assert.All(example, item => Assert.Contains(item.Key, allowed));
            Assert.All(Required(definition.ToolId), field => Assert.True(example.ContainsKey(field)));
        }
    }

    [Fact]
    public void Compatibility_catalog_projects_every_definition()
    {
        Assert.Equal(ExpectedOrder, AgentModelToolContractCatalog.ToolIds);
        Assert.Equal(
            ["web.search", "web.fetch"],
            AgentModelToolContractCatalog.WebToolIds.OrderBy(
                id => Array.IndexOf(ExpectedOrder, id)));
    }

    private static JsonObject Properties(string toolId) =>
        AgentToolDefinitionCatalog.Find(toolId, "1.0.0")!
            .InputSchema["properties"]!
            .AsObject();

    private static string[] Required(string toolId) =>
        AgentToolDefinitionCatalog.Find(toolId, "1.0.0")!
            .InputSchema["required"]?
            .AsArray()
            .Select(item => item!.GetValue<string>())
            .ToArray()
        ?? [];
}
