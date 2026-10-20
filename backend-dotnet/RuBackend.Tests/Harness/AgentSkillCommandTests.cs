using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentSkillCommandTests
{
    private const string PythonSkill = "---\nname: py.required\ndescription: Needs Python\nrequires: python\n---\n\nRun the script.";

    [Fact]
    public async Task Skill_command_refuses_a_python_skill_without_its_body_when_python_is_absent()
    {
        using var tmp = TempWorkspace.Create();
        AddSkill(tmp.Path, "py.required", PythonSkill);
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        factory.Services.GetRequiredService<AgentCommandsService>().PythonAvailable = () => false;
        var runId = await CreateRunAsync(client, tmp.Path);

        using var response = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/commands", new { text = "/skill py.required" });
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("python_unavailable", JsonDocument.Parse(raw).RootElement.GetProperty("error_code").GetString());
        Assert.DoesNotContain("Run the script", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skill_command_returns_the_body_of_a_python_skill_when_python_is_present()
    {
        using var tmp = TempWorkspace.Create();
        AddSkill(tmp.Path, "py.required", PythonSkill);
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        factory.Services.GetRequiredService<AgentCommandsService>().PythonAvailable = () => true;
        var runId = await CreateRunAsync(client, tmp.Path);

        using var response = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/commands", new { text = "/skill py.required" });
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Run the script", json.GetProperty("data").GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skill_command_refuses_an_unknown_requirement_naming_the_token_without_its_body()
    {
        using var tmp = TempWorkspace.Create();
        AddSkill(tmp.Path, "node.required", "---\nname: node.required\ndescription: Needs Node\nrequires: node\n---\n\nRun the script.");
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        factory.Services.GetRequiredService<AgentCommandsService>().PythonAvailable = () => true;
        var runId = await CreateRunAsync(client, tmp.Path);

        using var response = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/commands", new { text = "/skill node.required" });
        var raw = await response.Content.ReadAsStringAsync();
        var error = JsonDocument.Parse(raw).RootElement;

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("unsupported_skill_requirement", error.GetProperty("error_code").GetString());
        Assert.Contains("node", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Run the script", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skill_command_without_requirements_never_probes_python()
    {
        using var tmp = TempWorkspace.Create();
        AddSkill(tmp.Path, "plain.skill", "---\nname: plain.skill\ndescription: Plain\n---\n\nPlain body.");
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        factory.Services.GetRequiredService<AgentCommandsService>().PythonAvailable = () => throw new InvalidOperationException("probe_called");
        var runId = await CreateRunAsync(client, tmp.Path);

        using var response = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/commands", new { text = "/skill plain.skill" });
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Plain body.", json.GetProperty("data").GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    private static void AddSkill(string workspacePath, string leaf, string content)
    {
        var directory = Directory.CreateDirectory(Path.Combine(workspacePath, "skills", leaf)).FullName;
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), content, new UTF8Encoding(false));
    }

    private static async Task<string> CreateRunAsync(HttpClient client, string workspacePath)
    {
        using var sessionResp = await client.PostAsync("/sessions/new", null);
        var sessionJson = await sessionResp.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = sessionJson.GetProperty("session_id").GetString()!;
        var body = new
        {
            session_id = sessionId,
            workspace_path = workspacePath,
            prompt = "skill command",
            preset = "standard",
            idempotency_key = "skill-command-" + Guid.NewGuid().ToString("N"),
        };
        using var resp = await client.PostAsJsonAsync("/agent/v1/runs", body);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        if (resp.StatusCode != HttpStatusCode.Created && resp.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"create failed {resp.StatusCode} {json}");
        }

        return json.GetProperty("run_id").GetString()!;
    }

    private sealed class TempWorkspace : IDisposable
    {
        public string Path { get; }
        public RuBackend.Business.DataPaths DataPaths { get; }
        private readonly string _root;

        private TempWorkspace(string root, string wsPath, RuBackend.Business.DataPaths dataPaths)
        {
            _root = root;
            Path = wsPath;
            DataPaths = dataPaths;
        }

        public static TempWorkspace Create()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ru-skill-command-" + Guid.NewGuid().ToString("N"));
            var ws = System.IO.Path.Combine(root, "ws");
            Directory.CreateDirectory(ws);
            var dataRoot = System.IO.Path.Combine(root, "data");
            Directory.CreateDirectory(dataRoot);
            var resolved = RuBackend.Business.DataPaths.Resolve(root, root, root, dataRoot, System.IO.Path.Combine(root, "logs"));
            return new TempWorkspace(root, ws, resolved);
        }

        public void Dispose()
        {
            try { SqliteTestPools.ClearHarnessDatabase(DataPaths.AgentHarnessDatabase); } catch { }
            try { Directory.Delete(_root, true); } catch { }
        }
    }
}
