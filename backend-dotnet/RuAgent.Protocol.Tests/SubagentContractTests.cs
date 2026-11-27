using System.Text.Json;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class SubagentContractTests
{
    [Theory]
    [InlineData(SubagentRole.Coding, "coding")]
    [InlineData(SubagentRole.Review, "review")]
    public void Roles_use_exact_lowercase_wire_tokens(SubagentRole role, string token)
    {
        Assert.Equal("\"" + token + "\"", JsonSerializer.Serialize(role));
        Assert.Equal(role, JsonSerializer.Deserialize<SubagentRole>("\"" + token + "\""));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("\"Coding\"")]
    [InlineData("\"REVIEW\"")]
    [InlineData("\"owner\"")]
    public void Invalid_role_wire_tokens_fail_closed(string token) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SubagentRole>(token));

    [Theory]
    [InlineData("agent_spawn", "{\"role\":\"review\",\"task\":\"inspect\",\"profile\":\"bad id\"}")]
    [InlineData("agent_spawn", "{\"role\":\"review\",\"task\":\"inspect\",\"profile\":\"\"}")]
    [InlineData("agent_spawn", "{\"role\":\"review\",\"task\":\"inspect\",\"admin\":true}")]
    [InlineData("agent_spawn", "{\"role\":\"owner\",\"task\":\"inspect\"}")]
    [InlineData("agent_send", "{\"agent_id\":\"a\",\"message\":\" \"}")]
    [InlineData("agent_wait", "{\"agent_ids\":[]}")]
    [InlineData("agent_wait", "{\"agent_ids\":[\"a\",\"a\"]}")]
    [InlineData("agent_wait", "{\"agent_ids\":[\"a\"],\"timeout_ms\":600001}")]
    [InlineData("agent_apply", "{\"agent_id\":\"a\",\"result_version\":0}")]
    [InlineData("agent_apply", "{\"agent_id\":\"a\",\"result_version\":4}")]
    [InlineData("agent_cancel", "{\"agent_id\":\"../other\"}")]
    [InlineData("agent_cancel", "{\"agent_id\":\"a\",\"agent_id\":\"b\"}")]
    [InlineData("agent_unknown", "{}")]
    public void Invalid_arguments_fail_closed(string toolId, string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(SubagentContractValidator.Validate(toolId, document.RootElement).IsValid);
    }

    [Fact]
    public void One_wait_may_name_any_number_of_children()
    {
        // A family has no identity ceiling, so a wait is not capped at the old eight ids.
        var ids = Enumerable.Range(0, 20).Select(n => "agent_" + n).ToArray();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { agent_ids = ids }));
        Assert.True(SubagentContractValidator.Validate("agent_wait", document.RootElement).IsValid);
        var schema = AgentToolDefinitionCatalog.Find("agent_wait", "1.0.0")!.InputSchema;
        Assert.False(schema["properties"]!["agent_ids"]!.AsObject().ContainsKey("maxItems"));
    }

    [Theory]
    [InlineData("{\"role\":\"review\",\"task\":\"inspect\",\"detached\":true,\"detached_request\":\"keep checking in the background\"}", true)]
    [InlineData("{\"role\":\"coding\",\"task\":\"fix\",\"detached\":false}", true)]
    [InlineData("{\"role\":\"review\",\"task\":\"inspect\",\"detached\":true}", false)]
    [InlineData("{\"role\":\"review\",\"task\":\"inspect\",\"detached_request\":\"run it in the background\"}", false)]
    [InlineData("{\"role\":\"review\",\"task\":\"inspect\",\"detached\":false,\"detached_request\":\"run it in the background\"}", false)]
    [InlineData("{\"role\":\"review\",\"task\":\"inspect\",\"detached\":\"true\",\"detached_request\":\"run it in the background\"}", false)]
    [InlineData("{\"role\":\"review\",\"task\":\"inspect\",\"detached\":true,\"detached_request\":\" \"}", false)]
    public void Detached_spawn_needs_the_flag_and_the_users_words_together(string json, bool valid)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(valid, SubagentContractValidator.Validate("agent_spawn", document.RootElement).IsValid);
    }

    [Fact]
    public void Detached_request_is_bounded()
    {
        var quote = new string('x', SubagentLimits.MaxDetachedRequestCharacters + 1);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            { role = "review", task = "inspect", detached = true, detached_request = quote }));
        Assert.False(SubagentContractValidator.Validate("agent_spawn", document.RootElement).IsValid);
    }

    [Fact]
    public void A_non_detached_spawn_request_hashes_as_before()
    {
        // Detached fields are omitted when absent, so replaying an older spawn keeps its request hash.
        var json = JsonSerializer.Serialize(new SubagentSpawnRequest("review", "inspect"));
        Assert.DoesNotContain("detached", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bounded_profile_id_is_valid_wire_arguments()
    {
        using var document = JsonDocument.Parse(
            "{\"role\":\"review\",\"task\":\"inspect\",\"profile\":\"reviewer_2\"}");
        Assert.True(SubagentContractValidator.Validate("agent_spawn", document.RootElement).IsValid);
    }

    [Fact]
    public void Every_definition_has_valid_example_and_fresh_schema()
    {
        var definitions = SubagentToolDefinitions.Get();
        Assert.NotEmpty(definitions);
        Assert.True(AgentToolDefinitionCatalog.Get().Count > definitions.Count);
        Assert.All(definitions, d => Assert.NotNull(AgentToolDefinitionCatalog.Find(d.ToolId, d.ToolVersion)));
        foreach (var definition in definitions)
        {
            using var json = JsonDocument.Parse(definition.ExampleArguments!.ToJsonString());
            Assert.True(SubagentContractValidator.Validate(definition.ToolId, json.RootElement).IsValid);
            var schema = definition.InputSchema;
            Assert.False(schema["additionalProperties"]!.GetValue<bool>());
            schema["injected"] = true;
            Assert.False(definition.InputSchema.ContainsKey("injected"));
        }
    }

    [Fact]
    public void Text_limits_count_UTF16_and_context_counts_UTF8()
    {
        Assert.True(ValidateSpawn(new string('x', 16384), new string('é', 32768)));
        Assert.False(ValidateSpawn(new string('x', 16385), ""));
        Assert.False(ValidateSpawn("task", new string('é', 32769)));
        using var malformed = JsonDocument.Parse("{\"role\":\"coding\",\"task\":\"task\",\"context\":\"\\ud800\"}");
        Assert.False(SubagentContractValidator.Validate("agent_spawn", malformed.RootElement).IsValid);
    }

    private static bool ValidateSpawn(string task, string context)
    {
        var json = JsonSerializer.SerializeToElement(new { role = "coding", task, context });
        return SubagentContractValidator.Validate("agent_spawn", json).IsValid;
    }
}
