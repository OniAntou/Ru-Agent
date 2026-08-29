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
    public void Web_options_are_disabled_and_do_not_advertise_credentials_by_default()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());

        Assert.False(new WebOptions().Enabled);
        Assert.DoesNotContain(catalog.Registry.List(), binding => binding.Manifest.Id.StartsWith("web.", StringComparison.Ordinal));
    }

    [Fact]
    public void Enabled_web_catalog_exposes_bounded_contracts_only_when_configured()
    {
        var options = new WebOptions(true, new Uri("https://search.example.test/"));
        var executors = ToolBindingRegistryTests.ExecutableStubs();
        executors["web.search"] = Stub;
        executors["web.fetch"] = Stub;
        var catalog = new ToolCatalog(executors, options);

        var search = catalog.Registry.Get("web.search", "1.0.0").Manifest;
        var fetch = catalog.Registry.Get("web.fetch", "1.0.0").Manifest;
        Assert.Contains(ToolScope.WebSearch, search.Scopes);
        Assert.Contains(ToolScope.WebFetch, fetch.Scopes);
        ToolSchemaValidator.Validate(search.InputSchema, new JsonObject { ["query"] = "hello" });
        ToolSchemaValidator.Validate(fetch.InputSchema, new JsonObject { ["url"] = "https://example.com" });
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(search.InputSchema, new JsonObject()));
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(fetch.InputSchema, new JsonObject { ["url"] = "https://example.com", ["secret"] = "no" }));

        var agentScopes = CapabilityProfile.Agent.Scopes();
        Assert.DoesNotContain(ToolScope.WebSearch, agentScopes);
        Assert.DoesNotContain(ToolScope.WebFetch, agentScopes);
    }

    [Fact]
    public void Web_options_reject_private_or_oversized_search_configuration()
    {
        Assert.Throws<ArgumentException>(() => new WebOptions(true, new Uri("http://127.0.0.1/search")));
        Assert.Throws<ArgumentException>(() => new WebOptions(true, new Uri("http://[::ffff:127.0.0.1]/search")));
        Assert.Throws<ArgumentException>(() => new WebOptions(true, new Uri("https://user:password@example.com/search")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebOptions(true, null, WebOptions.MaxConfiguredResponseBytes + 1));
    }

    private static ValueTask<ToolExecutionOutcome> Stub(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ToolExecutionOutcome.Succeeded(
            new JsonObject(), ToolVerificationStates.NotRequired, "stub"));
}
