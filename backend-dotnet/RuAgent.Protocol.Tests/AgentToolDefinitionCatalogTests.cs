using System.Text.Json.Nodes;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class AgentToolDefinitionCatalogTests
{
    private static readonly string[] ExpectedOrder =
    [
        "read",
        "list",
        "help",
        "todo_write",
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
        "system.volume",        "files.list",
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
        Assert.Equal(11, definitions.Count);
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
    public void Read_list_and_help_are_single_purpose_schemas_without_a_discriminator()
    {
        foreach (var op in new[] { "write", "delete", "restore" })
        {
            var write = Properties("write");
            Assert.False(write.ContainsKey("expected_sha256"));
        }

        // read carries no discriminator and none of the operations that moved out.
        var read = Properties("read");
        Assert.Equal(["path"], Required("read"));
        Assert.Equal(
            new[] { "limit", "offset", "path" },
            read.Select(item => item.Key).OrderBy(key => key, StringComparer.Ordinal));
        Assert.False(read.ContainsKey("op"));
        Assert.False(read.ContainsKey("max_entries"));
        Assert.False(read.ContainsKey("query"));
        Assert.False(read.ContainsKey("max_results"));
        Assert.False(read.ContainsKey("regex"));
        Assert.Equal(256 * 1024, read["limit"]!["maximum"]!.GetValue<int>());

        // list keeps read op=list's argument shape and has no required field.
        var list = Properties("list");
        Assert.Empty(Required("list"));
        Assert.Equal(
            new[] { "max_entries", "path" },
            list.Select(item => item.Key).OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(1, list["max_entries"]!["minimum"]!.GetValue<int>());
        Assert.Equal(512, list["max_entries"]!["maximum"]!.GetValue<int>());

        // help keeps read op=help's argument shape.
        var help = Properties("help");
        Assert.Equal(["query"], Required("help"));
        Assert.Equal(1, help["query"]!["minLength"]!.GetValue<int>());
        Assert.Equal(256, help["query"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(1, help["max_results"]!["minimum"]!.GetValue<int>());
        Assert.Equal(5, help["max_results"]!["maximum"]!.GetValue<int>());

        Assert.Equal(["op"], Required("write"));

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
            .Where(definition => definition.ToolId is "read" or "list" or "write" or "edit" or "grep" or "glob"))
        {
            Assert.Contains("absolute", definition.Description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("preserve", definition.Description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("session", definition.Description, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Help_description_does_not_claim_workspace_path_access()
    {
        var help = AgentToolDefinitionCatalog.Find("help", "1.0.0")!.Description;

        Assert.DoesNotContain("absolute", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bundled", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never reads workspace files", help, StringComparison.Ordinal);
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
