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
    [InlineData("agent_spawn", "{\"role\":\"review\",\"task\":\"inspect\",\"admin\":true}")]
    [InlineData("agent_spawn", "{\"role\":\"owner\",\"task\":\"inspect\"}")]
    [InlineData("agent_send", "{\"agent_id\":\"a\",\"message\":\" \"}")]
    [InlineData("agent_wait", "{\"agent_ids\":[]}")]
    [InlineData("agent_wait", "{\"agent_ids\":[\"a\",\"a\"]}")]
    [InlineData("agent_wait", "{\"agent_ids\":[\"a\"],\"timeout_ms\":60001}")]
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
