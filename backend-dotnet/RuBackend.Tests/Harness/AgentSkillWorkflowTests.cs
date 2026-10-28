using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Context;
using RuBackend.Business;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentSkillWorkflowTests
{
    [Fact]
    public async Task Catalog_is_available_before_a_run_and_preserves_manual_only_metadata()
    {
        using var fixture = new Fixture();
        fixture.WriteSkill("verify", "---\nname: Verification\ndescription: >\n  Verify changed\n  code carefully.\ndisable-model-invocation: true\n---\nPrivate workflow.");
        using var factory = new BackendWebApplicationFactory(dataPaths: fixture.Paths);
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.GetAsync("/agent/v1/skills?workspace_path=" + Uri.EscapeDataString(fixture.Workspace));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var catalog = await response.Content.ReadFromJsonAsync<JsonElement>();
        var skill = catalog.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "verify");
        Assert.False(skill.GetProperty("model_invocable").GetBoolean());
        Assert.True(skill.GetProperty("user_invocable").GetBoolean());
        Assert.Contains("Verify changed code carefully.", skill.GetProperty("description").GetString());
        Assert.Equal(Path.Combine(fixture.Workspace, "skills", "verify"), skill.GetProperty("resource_base").GetString());
        Assert.DoesNotContain("Private workflow.", catalog.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_chat_invocation_captures_workflow_arguments_and_survives_a_disk_edit()
    {
        using var fixture = new Fixture();
        fixture.WriteSkill("verify", "---\nname: verify\ndescription: Verify code\ndisable-model-invocation: true\n---\nCheck $ARGUMENTS using the original checklist.");
        using var factory = new BackendWebApplicationFactory(dataPaths: fixture.Paths);
        using var client = factory.CreateAuthenticatedClient();
        var run = new RunId(await CreateRunAsync(client, fixture.Workspace, "/skill verify src/main.cs"));
        var payloads = factory.Services.GetRequiredService<HarnessPayloadStore>();
        await payloads.CaptureBaseContextAsync(run, [], DateTimeOffset.UtcNow, CancellationToken.None);
        var options = factory.Services.GetRequiredService<HarnessSqliteOptions>();
        var source = new SqliteModelContextSource(options, payloads);
        var first = await source.LoadAsync(run, CancellationToken.None);
        var workflow = Assert.Single(first.SessionBase.Concat(first.RunLocal), entry => entry.SourceId == "base:active-skills");
        Assert.Contains("Check src/main.cs using the original checklist.", workflow.Content);
        Assert.Equal(ModelContextRole.User, workflow.Role);
        Assert.Contains(first.RunLocal, entry => entry.Content == "/skill verify src/main.cs");

        fixture.WriteSkill("verify", "---\nname: verify\ndescription: Changed\n---\nNew checklist must not replace the captured workflow.");
        var afterRestart = await new SqliteModelContextSource(options, payloads).LoadAsync(run, CancellationToken.None);
        Assert.Contains(afterRestart.SessionBase.Concat(afterRestart.RunLocal), entry => entry.Content?.Contains("original checklist", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(afterRestart.SessionBase.Concat(afterRestart.RunLocal), entry => entry.Content?.Contains("New checklist", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Run_command_activates_a_workflow_instead_of_only_returning_its_body()
    {
        using var fixture = new Fixture();
        fixture.WriteSkill("verify", "---\nname: verify\ndescription: Verify code\n---\nUse the durable command checklist.");
        using var factory = new BackendWebApplicationFactory(dataPaths: fixture.Paths);
        using var client = factory.CreateAuthenticatedClient();
        var run = new RunId(await CreateRunAsync(client, fixture.Workspace, "Review code"));
        using var command = await client.PostAsJsonAsync($"/agent/v1/runs/{run.Value}/commands", new { text = "/skill verify" });
        Assert.Equal(HttpStatusCode.OK, command.StatusCode);
        Assert.True((await command.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("activated").GetBoolean());
        var payloads = factory.Services.GetRequiredService<HarnessPayloadStore>();
        await payloads.CaptureBaseContextAsync(run, [], DateTimeOffset.UtcNow, CancellationToken.None);
        var source = new SqliteModelContextSource(factory.Services.GetRequiredService<HarnessSqliteOptions>(), payloads);
        var context = await source.LoadAsync(run, CancellationToken.None);
        Assert.Contains(context.SessionBase.Concat(context.RunLocal), entry => entry.Content?.Contains("durable command checklist", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Skill_catalog_and_settings_require_authentication()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        using var catalog = await client.GetAsync("/agent/v1/skills");
        using var settings = await client.PostAsJsonAsync("/agent/v1/skills/settings", new { action = "refresh" });
        Assert.Equal(HttpStatusCode.Unauthorized, catalog.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, settings.StatusCode);
    }

    private static async Task<string> CreateRunAsync(HttpClient client, string workspace, string prompt)
    {
        using var session = await client.PostAsync("/sessions/new", null);
        var id = (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session_id").GetString();
        using var created = await client.PostAsJsonAsync("/agent/v1/runs", new
        {
            session_id = id, workspace_path = workspace, prompt,
            preset = "standard", idempotency_key = Guid.NewGuid().ToString("N"),
        });
        var body = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("run_id").GetString()!;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-skill-workflow-" + Guid.NewGuid().ToString("N"));
        public string Workspace { get; }
        public DataPaths Paths { get; }
        public Fixture()
        {
            Workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
            var data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
            Paths = DataPaths.Resolve(_root, _root, _root, data, Path.Combine(_root, "logs"));
        }
        public void WriteSkill(string id, string text)
        {
            var folder = Directory.CreateDirectory(Path.Combine(Workspace, "skills", id)).FullName;
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), text);
        }
        public void Dispose()
        {
            SqliteTestPools.ClearHarnessDatabase(Paths.AgentHarnessDatabase);
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
