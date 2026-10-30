using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuBackend.Business.Settings;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class AgentRunIdempotencyTests
{
    [Fact]
    public async Task Selected_admission_persists_the_intent_and_same_key_cannot_change_it()
    {
        using var tmp = TempWorkspace.Create(); using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient(); var session = await CreateSessionAsync(client);
        var body = new { session_id = session, workspace_path = tmp.Path, prompt = "selection race", preset = "standard",
            idempotency_key = "same-selected-key", provider_selection = new { backend = "OpenAI", model = "old-model" } };
        using var created = await client.PostAsJsonAsync("/agent/v1/runs/selected", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var root = await created.Content.ReadFromJsonAsync<JsonElement>(); var id = new RunId(root.GetProperty("run_id").GetString()!);
        var store = factory.Services.GetRequiredService<SqliteAgentRunProviderSelectionStore>();
        Assert.Equal(new RunProviderIntent("OpenAI", "old-model"), await store.GetIntentAsync(id, default));
        var settings = factory.Services.GetRequiredService<ISettingsStore>();
        await settings.SaveAsync((await settings.ReadAsync(default)) with { Backend = "Anthropic", AnthropicModel = "changed-default" }, default);
        using var replay = await client.PostAsJsonAsync("/agent/v1/runs/selected", body);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var conflict = await client.PostAsJsonAsync("/agent/v1/runs/selected", new { body.session_id, body.workspace_path, body.prompt, body.preset,
            body.idempotency_key, provider_selection = new { backend = "OpenAI", model = "new-model" } });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency_conflict", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error_code").GetString());
    }

    [Theory]
    [InlineData(null, null)] [InlineData("", "model")] [InlineData("OpenAI", "")]
    public async Task Selected_route_refuses_missing_or_invalid_intent(string? backend, string? model)
    {
        using var tmp = TempWorkspace.Create(); using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/agent/v1/runs/selected", new { provider_selection = backend is null ? null : new { backend, model } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Selected_image_validation_uses_the_submitted_provider_and_model()
    {
        using var tmp = TempWorkspace.Create(); using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient(); var session = await CreateSessionAsync(client);
        var service = factory.Services.GetRequiredService<RuBackend.Harness.AgentRunCreationService>();
        (string Backend, string Model)? checkedSelection = null;
        service.DeclaredProviderVision = (backend, model, _) => { checkedSelection = (backend, model); return Task.FromResult<bool?>(true); };
        var settings = factory.Services.GetRequiredService<ISettingsStore>();
        await settings.SaveAsync((await settings.ReadAsync(default)) with { Backend = "Anthropic", AnthropicModel = "text-default" }, default);
        using var created = await client.PostAsJsonAsync("/agent/v1/runs/selected", new {
            session_id = session, workspace_path = tmp.Path, prompt = "image uses intended provider", preset = "standard",
            idempotency_key = "image-intent", provider_selection = new { backend = "OpenAI", model = "gpt-4o" },
            image_attachments = new[] { "data:image/png;base64,AA==" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(("OpenAI", "gpt-4o"), checkedSelection);
    }
}
