using System.Text.Json.Nodes;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class AgentToolDefinitionCatalogTests
{
    private static readonly string[] ExpectedOrder =
    [
        "read",
        "grep",
        "glob",
        "write",
        "edit",
        "bash",
        "web_search",
        "web_fetch",
    ];

    private static readonly string[] RetiredIds =
    [
        "system.time",
        "system.status",
        "app.list",
        "notes.create",
        "system.volume",
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
    ];

    [Fact]
    public void Get_returns_exact_stable_inventory_and_retired_ids_are_absent()
    {
        var definitions = AgentToolDefinitionCatalog.Get();

        Assert.Equal(ExpectedOrder, definitions.Select(item => item.ToolId));
        Assert.Equal(8, definitions.Count);
        Assert.All(RetiredIds, id => Assert.Null(
            AgentToolDefinitionCatalog.Find(id, AgentToolDefinitionCatalog.CurrentVersion)));
    }

    [Fact]
    public void Schema_and_example_access_return_fresh_nodes()
    {
        var definition = AgentToolDefinitionCatalog.Find("read", "1.0.0")!;

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
        foreach (var op in new[] { "write", "delete", "restore" })
        {
            var write = Properties("write");
            Assert.False(write.ContainsKey("expected_sha256"));
        }

        var read = Properties("read");
        Assert.False(read.ContainsKey("regex"));

        Assert.Equal(["op"], Required("write"));
        Assert.Equal(["op"], Required("read"));

        Assert.Equal(256 * 1024, read["limit"]!["maximum"]!.GetValue<int>());

        var grep = Properties("grep");
        Assert.True(grep.ContainsKey("is_regex"));
        Assert.False(grep.ContainsKey("regex"));
        Assert.Equal(["query"], Required("grep"));
        Assert.Equal(1, grep["max_results"]!["minimum"]!.GetValue<int>());
        Assert.Equal(64, grep["max_results"]!["maximum"]!.GetValue<int>());

        var glob = Properties("glob");
        Assert.Equal(["pattern"], Required("glob"));
        Assert.Equal(1, glob["max_results"]!["minimum"]!.GetValue<int>());
        Assert.Equal(64, glob["max_results"]!["maximum"]!.GetValue<int>());
    }

    [Fact]
    public void Bash_tool_exposes_optional_read_only_and_write_set_contract()
    {
        var properties = Properties("bash");
        Assert.Equal("boolean", properties["read_only"]!["type"]!.GetValue<string>());
        Assert.Equal("array", properties["write_set"]!["type"]!.GetValue<string>());
        Assert.Equal("string", properties["write_set"]!["items"]!["type"]!.GetValue<string>());
        Assert.DoesNotContain("read_only", Required("bash"));
        Assert.DoesNotContain("write_set", Required("bash"));
    }

    [Fact]
    public void Every_definition_has_valid_identity_description_scope_schema_and_example()
    {
        foreach (var definition in AgentToolDefinitionCatalog.Get())
        {
            Assert.Equal("1.0.0", definition.ToolVersion);
            Assert.Matches("^[a-z][a-z0-9_]*$", definition.ToolId);
            Assert.False(string.IsNullOrWhiteSpace(definition.Description));
            Assert.Contains(definition.Scope, AgentToolScopes.All);
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
    public void Model_contract_catalog_projects_the_entire_canonical_catalog()
    {
        Assert.Equal(ExpectedOrder, AgentModelToolContractCatalog.ToolIds);
        Assert.Equal(ExpectedOrder, AgentModelToolContractCatalog.Get().Select(item => item.ToolId));
        Assert.Equal(["web_fetch", "web_search"], AgentModelToolContractCatalog.WebToolIds.OrderBy(id => id, StringComparer.Ordinal));

        foreach (var retiredId in RetiredIds)
        {
            Assert.Null(AgentModelToolContractCatalog.Find(retiredId, "1.0.0"));
            Assert.Throws<KeyNotFoundException>(() => AgentModelToolContractCatalog.Get(retiredId, "1.0.0"));
        }
    }

    [Fact]
    public void Read_write_descriptions_preserve_user_supplied_absolute_paths()
    {
        foreach (var definition in AgentToolDefinitionCatalog.Get()
            .Where(definition => definition.ToolId is "read" or "write" or "edit" or "grep" or "glob"))
        {
            Assert.Contains("absolute", definition.Description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("preserve", definition.Description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("session", definition.Description, StringComparison.OrdinalIgnoreCase);
        }
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
