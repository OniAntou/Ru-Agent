using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Client;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentPolicyControllerTests
{
    private sealed class FakePolicyClient : IAgentPolicyClient
    {
        public List<JsonElement> ListResult { get; set; } = new();
        public List<string> Deleted { get; } = new();
        public List<object> Created { get; } = new();
        public bool ListCalled;
        public bool CreateCalled;
        public bool DeleteCalled;

        public Task<IReadOnlyList<JsonElement>> ListPoliciesAsync(string? scope = null, string? workspaceId = null, CancellationToken cancellationToken = default)
        {
            ListCalled = true;
            return Task.FromResult<IReadOnlyList<JsonElement>>(ListResult);
        }

        public Task<JsonElement> CreatePolicyAsync(object policyBody, bool interactive, CancellationToken cancellationToken = default)
        {
            CreateCalled = true;
            Created.Add(policyBody);
            return Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());
        }

        public Task DeletePolicyAsync(string ruleId, bool interactive, CancellationToken cancellationToken = default)
        {
            DeleteCalled = true;
            Deleted.Add(ruleId);
            return Task.CompletedTask;
        }

        public Task<JsonElement> SetSessionPresetAsync(string sessionId, string preset, long expectedVersion, bool interactive, CancellationToken cancellationToken = default, string? snapshotToken = null)
            => Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());

        public Task<JsonElement> RestoreSessionPresetAsync(string sessionId, string snapshotToken, CancellationToken cancellationToken = default)
            => Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());
    }

    [Fact]
    public async Task Load_parses_and_collects_policies()
    {
        var fake = new FakePolicyClient();
        using var doc = JsonDocument.Parse("""
        {"id":"rule-1","scope":"terminal","kind":"allow","description":"d","enabled":true,"version":1}
        """);
        fake.ListResult.Add(doc.RootElement.Clone());

        var controller = new AgentPolicyController(fake);
        await controller.LoadAsync();

        Assert.True(fake.ListCalled);
        Assert.Single(controller.Policies);
        Assert.Equal("rule-1", controller.Policies[0].Id);
        Assert.False(controller.IsBusy);
    }

    [Fact]
    public async Task Create_sends_body_and_adds_to_list()
    {
        var fake = new FakePolicyClient();
        var controller = new AgentPolicyController(fake);

        await controller.CreateAsync(new AgentPolicyModel("rule-2", "fs", "deny", "no writes", false, 0));

        Assert.True(fake.CreateCalled);
        var body = Assert.IsType<Dictionary<string, object?>>(Assert.Single(fake.Created));
        Assert.Equal("fs", body["scope"]);
        Assert.Equal("deny", body["decision"]);
        Assert.DoesNotContain("kind", body.Keys);
        Assert.DoesNotContain("description", body.Keys);
        Assert.DoesNotContain("enabled", body.Keys);
        Assert.IsType<JsonElement>(body["matcher"]);
        Assert.Single(controller.Policies);
        Assert.Equal("rule-2", controller.Policies[0].Id);
    }

    [Fact]
    public async Task Delete_calls_client_and_removes()
    {
        var fake = new FakePolicyClient();
        using var doc = JsonDocument.Parse("""{"id":"rule-1","scope":"terminal","kind":"allow","description":"d","enabled":true,"version":1}""");
        fake.ListResult.Add(doc.RootElement.Clone());
        var controller = new AgentPolicyController(fake);
        await controller.LoadAsync();

        await controller.DeleteAsync("rule-1");

        Assert.True(fake.DeleteCalled);
        Assert.Empty(controller.Policies);
    }
}
