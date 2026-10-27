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
        "web_search",
        "web_fetch",
        "skill",
        "ask_user",
        "mcp",
        "computer",
        "agent_spawn",
        "agent_send",
        "agent_wait",
        "agent_cancel",
        "agent_apply",
        "run_code",
        "apply_patch",
        "read_file",
        "write_file",
        "edit_file",
        "file_delete",
        "file_restore",
        "powershell",
        "run_process",
        "terminal_read",
        "terminal_stop",
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
        "write",
        "edit",
        "bash",
    ];

    [Fact]
    public void Terminal_verification_metadata_is_optional_and_expected_exit_is_bounded()
    {
        var properties = Properties("powershell");
        Assert.Equal(new[] { "command", "inspection", "verification", "expected_failure" },
            properties["purpose"]!["enum"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Equal(int.MinValue, properties["expected_exit_code"]!["minimum"]!.GetValue<int>());
        Assert.Equal(int.MaxValue, properties["expected_exit_code"]!["maximum"]!.GetValue<int>());
        Assert.DoesNotContain("purpose", Required("powershell"));
        Assert.DoesNotContain("expected_exit_code", Required("powershell"));
    }

    [Fact]
    public void Get_returns_exact_stable_inventory_and_retired_ids_are_absent()
    {
        var definitions = AgentToolDefinitionCatalog.Get();

        Assert.Equal(ExpectedOrder, definitions.Select(item => item.ToolId));
        Assert.Equal(28, definitions.Count);
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
    public void Terminal_tools_expose_optional_write_claims_without_granting_permissions()
    {
        foreach (var toolId in new[] { "powershell", "run_process" })
        {
            var properties = Properties(toolId);
            Assert.Equal("boolean", properties["read_only"]!["type"]!.GetValue<string>());
            Assert.Equal("array", properties["write_set"]!["type"]!.GetValue<string>());
            Assert.Equal("string", properties["write_set"]!["items"]!["type"]!.GetValue<string>());
            Assert.DoesNotContain("read_only", Required(toolId));
            Assert.DoesNotContain("write_set", Required(toolId));
            Assert.DoesNotContain("sandbox", properties.Select(property => property.Key));
        }
    }

    [Fact]
    public void File_mutation_schemas_are_explicit_and_bounded()
    {
        var write = Properties("write_file");
        Assert.Equal(["path", "content"], Required("write_file"));
        Assert.Equal(4 * 1024 * 1024, write["content"]!["maxLength"]!.GetValue<int>());
        Assert.Equal("boolean", write["overwrite"]!["type"]!.GetValue<string>());

        var edit = Properties("edit_file");
        Assert.Equal(["path", "edits"], Required("edit_file"));
        Assert.Equal(1, edit["edits"]!["minItems"]!.GetValue<int>());
        Assert.Equal(64, edit["edits"]!["maxItems"]!.GetValue<int>());
        Assert.Equal(["old_string", "new_string"], edit["edits"]!["items"]!["required"]!.AsArray()
            .Select(item => item!.GetValue<string>()));
        Assert.Equal(1, edit["edits"]!["items"]!["properties"]!["old_string"]!["minLength"]!.GetValue<int>());
    }

    [Fact]
    public void Terminal_command_schemas_have_distinct_public_shapes_and_bounded_arguments()
    {
        var powershell = Properties("powershell");
        Assert.Equal(["script"], Required("powershell"));
        Assert.True(powershell.ContainsKey("script"));
        Assert.False(powershell.ContainsKey("mode"));
        Assert.False(powershell.ContainsKey("shell"));

        var process = Properties("run_process");
        Assert.Equal(["executable"], Required("run_process"));
        Assert.True(process.ContainsKey("arguments"));
        Assert.Equal(128, process["arguments"]!["maxItems"]!.GetValue<int>());
        Assert.Equal(4096, process["arguments"]!["items"]!["maxLength"]!.GetValue<int>());
        Assert.False(process.ContainsKey("script"));

        Assert.Equal(["execution_id"], Required("terminal_read"));
        Assert.Equal(["execution_id"], Required("terminal_stop"));
        Assert.Equal(0, Properties("terminal_read")["offset"]!["minimum"]!.GetValue<int>());
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
    [InlineData("write_file", false)]
    [InlineData("edit_file", false)]
    [InlineData("powershell", false)]
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
            .Where(definition => definition.ToolId is "read" or "list" or "write_file" or "edit_file" or "grep" or "glob"))
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
    public void File_mutation_guidance_names_overwrite_and_exact_edit_source()
    {
        var write = AgentToolDefinitionCatalog.Find("write_file", "1.0.0")!;
        Assert.Contains("overwrite=true", write.Description);
        Assert.Contains("path", write.InputSchema["required"]!.AsArray().Select(item => item!.GetValue<string>()));

        var edit = AgentToolDefinitionCatalog.Find("edit_file", "1.0.0")!;
        Assert.Contains("exact", edit.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("copied from a fresh read", edit.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path", edit.InputSchema["required"]!.AsArray().Select(item => item!.GetValue<string>()));
    }

    [Fact]
    public void Run_process_description_keeps_executable_separate_from_literal_arguments()
    {
        var process = AgentToolDefinitionCatalog.Find("run_process", "1.0.0")!;

        Assert.Contains("literal arguments", process.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("without shell parsing", process.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["executable"], Required("run_process"));
        Assert.Contains("arguments", Properties("run_process").Select(property => property.Key));
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
