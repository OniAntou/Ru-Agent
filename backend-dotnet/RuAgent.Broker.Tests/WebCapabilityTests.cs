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
    public void Web_ops_live_inside_the_read_tool_and_require_no_extra_binding()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());

        Assert.False(new WebOptions().Enabled);
        var read = catalog.Registry.Get("read", "1.0.0").Manifest;
        Assert.Contains(ToolScope.SystemRead, read.Scopes);
        Assert.Contains(ToolScope.WebSearch, read.Scopes);
        Assert.Contains(ToolScope.WebFetch, read.Scopes);
        ToolSchemaValidator.Validate(read.InputSchema, new JsonObject { ["op"] = "web_search", ["query"] = "hello" });
        ToolSchemaValidator.Validate(read.InputSchema, new JsonObject { ["op"] = "web_fetch", ["url"] = "https://example.com" });
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(read.InputSchema, new JsonObject { ["query"] = "hello" }));
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(read.InputSchema, new JsonObject { ["op"] = "web_fetch", ["url"] = "https://example.com", ["secret"] = "no" }));

        var agentScopes = CapabilityProfile.Agent.Scopes();
        Assert.Contains(ToolScope.WebSearch, agentScopes);
        Assert.Contains(ToolScope.WebFetch, agentScopes);
    }

    [Fact]
    public void Web_options_reject_private_or_oversized_search_configuration()
    {
        Assert.Throws<ArgumentException>(() => new WebOptions(true, new Uri("http://127.0.0.1/search")));
        Assert.Throws<ArgumentException>(() => new WebOptions(true, new Uri("http://[::ffff:127.0.0.1]/search")));
        Assert.Throws<ArgumentException>(() => new WebOptions(true, new Uri("https://user:password@example.com/search")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebOptions(true, null, WebOptions.MaxConfiguredResponseBytes + 1));
    }
}
