using System.Text.Json.Nodes;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ToolAcceptanceMatrixTests
{
    [Theory]
    [InlineData(8)]
    public void Runtime_catalog_binds_every_available_canonical_definition(
        int expectedBindings)
    {
        var executors = ToolBindingRegistryTests.ExecutableStubs();
        var options = new WebOptions();

        var bindings = new ToolCatalog(executors, options).Registry.List();

        Assert.Equal(expectedBindings, bindings.Count);
        foreach (var binding in bindings)
        {
            var definition = AgentToolDefinitionCatalog.Find(
                binding.Manifest.Id,
                binding.Manifest.Version);
            Assert.NotNull(definition);
            Assert.Equal(
                AgentToolDefinitionCatalog.ComputeSchemaDigest(definition.InputSchema),
                AgentToolDefinitionCatalog.ComputeSchemaDigest(binding.Manifest.InputSchema));
            Assert.True(binding.ModelEnabled);
        }

        var expectedIds = AgentToolDefinitionCatalog.Get()
            .Select(definition => definition.ToolId)
            .OrderBy(id => id, StringComparer.Ordinal);
        Assert.Equal(expectedIds, bindings.Select(binding => binding.Manifest.Id));
    }

    [Fact]
    public void Model_contracts_are_exactly_the_model_enabled_canonical_definitions()
    {
        var expected = AgentToolDefinitionCatalog.Get()
            .Select(definition => definition.ToolId)
            .ToArray();

        Assert.Equal(8, expected.Length);
        Assert.Equal(expected, AgentModelToolContractCatalog.ToolIds);
    }

    private static ValueTask<ToolExecutionOutcome> Succeeded(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ToolExecutionOutcome.Succeeded(
            new JsonObject(),
            ToolVerificationStates.NotRequired,
            "completed"));
}
