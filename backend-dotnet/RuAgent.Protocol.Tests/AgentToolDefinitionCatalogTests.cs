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
        "skill",
        "ask_user",
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
        Assert.Equal(13, definitions.Count);
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
        Assert.Equal(4 * 1024 * 1024, read["limit"]!["maximum"]!.GetValue<int>());

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
        Assert.Equal(128, grep["max_results"]!["maximum"]!.GetValue<int>());

        var glob = Properties("glob");
        Assert.Equal(["pattern"], Required("glob"));
        Assert.Equal(1, glob["max_results"]!["minimum"]!.GetValue<int>());
        Assert.Equal(128, glob["max_results"]!["maximum"]!.GetValue<int>());
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
    public void Bash_tool_does_not_expose_a_sandbox_override()
    {
        var properties = Properties("bash");
        Assert.DoesNotContain("sandbox", Required("bash"));
        Assert.DoesNotContain("sandbox", properties.Select(property => property.Key));
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

    [Theory]
    [InlineData("read", true)]
    [InlineData("list", true)]
    [InlineData("help", true)]
    [InlineData("skill", true)]
    [InlineData("grep", true)]
    [InlineData("glob", true)]
    [InlineData("web_search", true)]
    [InlineData("web_fetch", true)]
    [InlineData("todo_write", false)]
    [InlineData("write", false)]
    [InlineData("edit", false)]
    [InlineData("bash", false)]
    [InlineData("future_tool", false)]
    public void Broker_access_catalog_is_conservative_for_parallel_reads(
        string toolId,
        bool expected)
    {
        Assert.Equal(expected, RuAgent.Protocol.Harness.BrokerPolicyContracts.IsReadOnlyTool(toolId));
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
            Assert.Contains("workspace-relative", definition.Description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("absolute", definition.Description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("preserve", definition.Description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("session", definition.Description, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("src\\App.cs", AgentToolDefinitionCatalog.Find("read", "1.0.0")!.Description);
        Assert.Equal("src\\App.cs", AgentToolDefinitionCatalog.Find("read", "1.0.0")!.ExampleArguments!["path"]!.GetValue<string>());
    }

    [Fact]
    public void File_mutation_guidance_names_the_required_operation_and_exact_edit_source()
    {
        var write = AgentToolDefinitionCatalog.Find("write", "1.0.0")!;
        Assert.Contains("op=write", write.Description);
        Assert.Contains("overwrite=true", write.Description);
        Assert.Equal("src\\notes.txt", write.ExampleArguments!["path"]!.GetValue<string>());

        var edit = AgentToolDefinitionCatalog.Find("edit", "1.0.0")!;
        Assert.Contains("exact", edit.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("copied from a fresh read", edit.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("whitespace must match", edit.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("src\\notes.txt", edit.ExampleArguments!["path"]!.GetValue<string>());
    }

    [Fact]
    public void Bash_example_keeps_executable_out_of_the_argument_vector()
    {
        var bash = AgentToolDefinitionCatalog.Find("bash", "1.0.0")!;
        var example = bash.ExampleArguments!;

        Assert.Contains("separate from arguments", bash.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mode=shell with script", bash.Description, StringComparison.Ordinal);
        Assert.Contains("default shell is powershell.exe", bash.Description, StringComparison.Ordinal);
        Assert.Equal("node", example["executable"]!.GetValue<string>());
        Assert.Equal(new[] { "-e", "console.log('hi')" }, example["arguments"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.DoesNotContain("node", example["arguments"]!.AsArray().Select(item => item!.GetValue<string>()));
    }

    [Fact]
    public void Help_description_does_not_claim_workspace_path_access()
    {
        var help = AgentToolDefinitionCatalog.Find("help", "1.0.0")!.Description;

        Assert.DoesNotContain("absolute", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bundled", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never reads workspace files", help, StringComparison.Ordinal);
    }

    [Fact]
    public void Web_search_description_matches_the_raced_fallback_chain()
    {
        // The Broker races the configured primary against WebOptions'
        // DefaultFallbackEndpoints, so a description claiming one engine or
        // no fallback tells the model a behavior the adapter does not have.
        var search = AgentToolDefinitionCatalog.Find("web_search", "1.0.0")!.Description;

        Assert.Contains("fallback", search, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("only configured engine", search, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rather than being retried elsewhere", search, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bounded", search, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Skill_definition_loads_body_without_granting_capabilities()
    {
        var skill = AgentToolDefinitionCatalog.Find("skill", "1.0.0")!;
        Assert.Equal(AgentToolScopes.SystemRead, skill.Scope);
        Assert.Contains(skill.InputSchema["required"]!.AsArray(), item => item!.GetValue<string>() == "name");
        Assert.Contains("allowed-tools", skill.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never grants", skill.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ask_user_definition_is_host_intercepted_input_with_bounded_options()
    {
        var ask = AgentToolDefinitionCatalog.Find("ask_user", "1.0.0")!;

        Assert.Equal(AgentToolScopes.UserInput, ask.Scope);
        Assert.Contains(ask.InputSchema["required"]!.AsArray(), item => item!.GetValue<string>() == "question");
        var properties = Properties("ask_user");
        Assert.Equal(1, properties["question"]!["minLength"]!.GetValue<int>());
        Assert.Equal(2048, properties["question"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(8, properties["options"]!["maxItems"]!.GetValue<int>());
        Assert.Equal(512, properties["options"]!["items"]!["maxLength"]!.GetValue<int>());
        Assert.Contains("parks", ask.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("answer", ask.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Schema_digest_ignores_property_order()
    {
        // The Broker refuses to start when a manifest's parsed schema digests
        // differently from the Protocol-built one. Key order is not part of a
        // schema's meaning, so reordering keys must not change the digest --
        // otherwise an equivalent manifest written with different key order
        // breaks startup for a reason nobody can see in review.
        var built = AgentToolDefinitionCatalog.Find("read", "1.0.0")!.InputSchema;
        var reordered = new JsonObject
        {
            ["required"] = new JsonArray("path"),
            ["properties"] = new JsonObject
            {
                ["limit"] = new JsonObject { ["maximum"] = 4 * 1024 * 1024, ["minimum"] = 1, ["type"] = "integer" },
                ["offset"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
                ["path"] = new JsonObject { ["type"] = "string" },
            },
            ["additionalProperties"] = false,
            ["type"] = "object",
        };

        Assert.Equal(
            AgentToolDefinitionCatalog.ComputeSchemaDigest(reordered),
            AgentToolDefinitionCatalog.ComputeSchemaDigest(built));
    }

    [Fact]
    public void Schema_digest_still_separates_different_schemas()
    {
        var read = AgentToolDefinitionCatalog.Find("read", "1.0.0")!.InputSchema;
        var list = AgentToolDefinitionCatalog.Find("list", "1.0.0")!.InputSchema;
        Assert.NotEqual(
            AgentToolDefinitionCatalog.ComputeSchemaDigest(read),
            AgentToolDefinitionCatalog.ComputeSchemaDigest(list));
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
