using System.Text.Json;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentPolicyModelTests
{
    [Fact]
    public void Accepted_policy_is_parsed_bounded()
    {
        using var doc = JsonDocument.Parse("""
        {
          "id": "rule-1",
          "scope": "terminal",
          "kind": "allow",
          "description": "cho phép chạy lint",
          "enabled": true,
          "version": 3
        }
        """);

        Assert.True(AgentPolicyModel.TryParse(doc.RootElement, out var policy));
        Assert.Equal("rule-1", policy.Id);
        Assert.Equal("terminal", policy.Scope);
        Assert.Equal("allow", policy.Kind);
        Assert.Equal("cho phép chạy lint", policy.Description);
        Assert.True(policy.Enabled);
        Assert.Equal(3, policy.Version);
    }

    [Fact]
    public void Backend_policy_response_is_parsed_without_legacy_field_assumptions()
    {
        using var doc = JsonDocument.Parse("""
        {
          "rule_id": "rule_1",
          "scope": "global",
          "decision": "deny",
          "matcher": {
            "tool_id": "files.write",
            "canonical_path_pattern": ".env*"
          },
          "creation_order": 4,
          "expires_at": null,
          "version": 1
        }
        """);

        Assert.True(AgentPolicyModel.TryParse(doc.RootElement, out var policy));
        Assert.Equal("rule_1", policy.Id);
        Assert.Equal("deny", policy.Kind);
        Assert.Equal("files.write · .env*", policy.Description);
        Assert.Equal("files.write", policy.Matcher.GetProperty("tool_id").GetString());
        Assert.True(policy.Enabled);
    }

    [Theory]
    [InlineData("""{"id":"rule-1"}""")]
    [InlineData("""{"id":"rule-1","scope":"terminal","kind":"allow","unexpected":true}""")]
    [InlineData("""[]""")]
    [InlineData("""{"id":"   ","scope":"terminal","kind":"allow"}""")]
    public void Invalid_policy_is_rejected(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.False(AgentPolicyModel.TryParse(doc.RootElement, out _));
    }

    [Fact]
    public void Constructor_rejects_oversized_id()
    {
        Assert.Throws<ArgumentException>(() => new AgentPolicyModel(new string('r', 129), "s", "k", "d", true, 0));
    }
}
