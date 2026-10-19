using System.Text.Json;
using RuAgent.Protocol.Subagents;
using Xunit;
namespace RuAgent.Protocol.Tests;
public sealed class SubagentModelRequestTests
{
    [Fact]
    public void Inherited_spawn_retains_legacy_json_for_durable_idempotency_hashes()
    {
        Assert.Equal("{\"role\":\"review\",\"task\":\"Inspect\",\"context\":null}",
            JsonSerializer.Serialize(new SubagentSpawnRequest("review", "Inspect")));
        var requested = JsonSerializer.Serialize(new SubagentSpawnRequest("review", "Inspect", Model: "Claude Opus 4.5"));
        Assert.Contains("\"model\":\"Claude Opus 4.5\"", requested);
        Assert.DoesNotContain("provider_id", requested);
    }

    [Theory]
    [InlineData("{\"role\":\"review\",\"task\":\"Inspect parser\"}", true)]
    [InlineData("{\"role\":\"review\",\"task\":\"Inspect\",\"model\":\"Claude Opus 4.5\"}", true)]
    [InlineData("{\"role\":\"review\",\"task\":\"Inspect\",\"model\":\"\"}", false)]
    [InlineData("{\"role\":\"review\",\"task\":\"Inspect\",\"provider_id\":\"anthropic\"}", false)]
    public void Per_spawn_model_request_is_optional_bounded_and_requires_a_model_with_provider(string json, bool valid)
    {
        Assert.Equal(valid, SubagentContractValidator.Validate("agent_spawn", JsonSerializer.Deserialize<JsonElement>(json)).IsValid);
    }
}
