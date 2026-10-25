using System.Net;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol.Subagents;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Harness.Subagents;
using RuBackend.Providers;
using Xunit;
namespace RuBackend.Tests.Harness;
public sealed class SubagentModelSelectionProviderTests
{
    [Theory]
    [InlineData(true, true, true, null)]
    [InlineData(false, true, true, "subagent_model_unavailable_signed_in")]
    [InlineData(true, false, true, "subagent_model_unavailable_signed_in")]
    [InlineData(true, true, false, "subagent_model_user_request_required")]
    public async Task Override_requires_user_prompt_and_signed_in_provider_catalog(bool signedIn, bool available, bool requested, string? error)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var bytes = Encoding.UTF8.GetBytes(requested ? "Use Claude Opus 4.5 for the review subagent" : "Inspect parser");
        var run = await fixture.SeedRunAsync(payloads: [new(new("payload_" + new string('e', 32)),
            HarnessPayloadKind.UserPrompt, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, bytes.Length, bytes)]);
        var pins = new SqliteAgentRunProviderSelectionStore(fixture.Options);
        var parent = new AgentRunProviderSelection("Custom OpenAI", "custom", "chat_completions",
            "https://fixture.invalid/v1/", "main-model", "custom_api_key", "{\"ru_tool_profile\":\"coding_v2\"}", 1024, ModelLabel: "main-model", CallingMode: "native");
        await pins.PinAsync(run.Run.RunId, parent, default);
        var settings = new SettingsStore(DataPaths.Resolve(fixture.RootPath, fixture.RootPath, fixture.RootPath, fixture.RootPath));
        var current = (await settings.ReadAsync(default)) with { Backend = "Custom OpenAI", CustomModel = "main-model", CustomUrl = "https://fixture.invalid/v1" };
        await settings.SaveAsync(current, default);
        var credentials = new Credentials(signedIn);
        using var handler = new Handler(available);
        using var http = new HttpClient(handler);
        var catalog = new ProviderCatalogService(http, credentials, TimeProvider.System, Path.Combine(fixture.RootPath, "models-dev.json"));
        await using var snapshots = new AgentProviderSnapshotFactory(settings, credentials, catalog, pins, (_, _) => Task.FromResult(false));
        var models = new SubagentModelSelectionProvider(pins, settings, snapshots, fixture.PayloadStore, catalog, credentials);
        if (error is null)
        {
            var selected = await models.ResolveAsync(run.Run.RunId, SubagentRole.Review, default, "Claude Opus 4.5");
            Assert.Equal("claude-opus-4.5", selected.Model);
            Assert.Equal("Custom OpenAI", selected.Backend);
            Assert.Equal(parent, (await pins.GetAsync(run.Run.RunId, default))!.Selection);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<SubagentConfigurationException>(() => models.ResolveAsync(run.Run.RunId,
                SubagentRole.Review, default, "Claude Opus 4.5"));
            Assert.Equal(error, failure.Code);
        }
        if (!signedIn || !requested) Assert.Equal(0, handler.ModelRequests);
        // Even after settings change or sign-out, inheritance copies the durable pin without catalog I/O.
        credentials.SignedIn = false;
        var count = handler.ModelRequests;
        Assert.Equal(parent, await models.ResolveAsync(run.Run.RunId, SubagentRole.Coding, default));
        Assert.Equal(count, handler.ModelRequests);
    }
    private sealed class Credentials(bool signedIn) : ICredentialStore
    {
        public bool SignedIn = signedIn;
        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken ct) =>
            Task.FromResult<string?>(SignedIn && accountName == "custom_api_key" ? "synthetic-fixture-key" : null);
        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string serviceName, string accountName, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Handler(bool available) : HttpMessageHandler
    {
        public int ModelRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var modelsDev = request.RequestUri!.Host == "models.dev";
            if (!modelsDev) ModelRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(modelsDev ? "{\"invalid:provider\":{\"api\":\"https://untrusted.invalid/v1\"}}"
                : available ? "{\"data\":[{\"id\":\"claude-opus-4.5\"}]}" : "{\"data\":[{\"id\":\"other-model\"}]}") });
        }
    }
}
