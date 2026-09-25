using System.Text.Json.Nodes;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class WebCapabilityTests
{
    [Fact]
    public void Web_ops_live_as_standalone_tools_and_require_no_extra_binding()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());

        Assert.False(new WebOptions().Enabled);
        var search = catalog.Registry.Get("web_search", "1.0.0").Manifest;
        var fetch = catalog.Registry.Get("web_fetch", "1.0.0").Manifest;
        Assert.Contains(ToolScope.WebSearch, search.Scopes);
        Assert.Contains(ToolScope.WebFetch, fetch.Scopes);
        Assert.Equal(Policy.RiskClass.External, search.Risk);
        Assert.Equal(Policy.RiskClass.External, fetch.Risk);
        ToolSchemaValidator.Validate(search.InputSchema, new JsonObject { ["query"] = "hello" });
        ToolSchemaValidator.Validate(fetch.InputSchema, new JsonObject { ["url"] = "https://example.com" });
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(search.InputSchema, new JsonObject { ["query"] = "hello", ["secret"] = "no" }));
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(fetch.InputSchema, new JsonObject { ["url"] = "https://example.com", ["secret"] = "no" }));

        var read = catalog.Registry.Get("read", "1.0.0").Manifest;
        Assert.DoesNotContain(ToolScope.WebSearch, read.Scopes);
        Assert.DoesNotContain(ToolScope.WebFetch, read.Scopes);
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(read.InputSchema, new JsonObject { ["op"] = "web_search", ["query"] = "hello" }));
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(read.InputSchema, new JsonObject { ["op"] = "web_fetch", ["url"] = "https://example.com" }));

        var agentScopes = CapabilityProfile.Agent.Scopes();
        Assert.Contains(ToolScope.WebSearch, agentScopes);
        Assert.Contains(ToolScope.WebFetch, agentScopes);

        // The Agent profile carries every scope, so the v1 whole-manifest gate
        // allows unconditionally. Standalone web tools carry their own scope,
        // so each gate is exact: search needs WebSearch, fetch needs WebFetch.
        var v1 = new ToolPolicyEngine().Evaluate(
            new PolicyGrant("session-web", agentScopes, DateTimeOffset.UtcNow.AddMinutes(5)), search);
        Assert.True(v1.Allowed);
        Assert.True(v1.RequiresApproval);
    }

    [Fact]
    public void Web_options_reject_private_or_oversized_search_configuration()
    {
        Assert.Throws<ArgumentException>(() => new WebOptions(true, new Uri("http://127.0.0.1/search")));
        Assert.Throws<ArgumentException>(() => new WebOptions(true, new Uri("http://[::ffff:127.0.0.1]/search")));
        Assert.Throws<ArgumentException>(() => new WebOptions(true, new Uri("https://user:password@example.com/search")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebOptions(true, null, WebOptions.MaxConfiguredResponseBytes + 1));
    }

    [Fact]
    public void Web_options_name_exactly_one_search_engine()
    {
        var options = new WebOptions(enabled: true);

        Assert.Equal("https://www.google.com/search", options.SearchEndpoint?.AbsoluteUri.TrimEnd('/'));
        Assert.True(options.SearchEnabled);
        Assert.True(options.FetchEnabled);
    }
}
