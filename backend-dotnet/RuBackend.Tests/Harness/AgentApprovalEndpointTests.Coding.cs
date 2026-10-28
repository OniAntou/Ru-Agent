using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Privacy;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class AgentApprovalEndpointTests
{
    [Theory]
    [InlineData("terminal_read")]
    [InlineData("terminal_stop")]
    public async Task Explicit_ask_for_terminal_lifecycle_can_be_approved_once(string tool)
    {
        var workspace = CreateTempWorkspace(out var paths);
        using var factory = new BackendWebApplicationFactory(dataPaths: paths,
            modelStepRunner: new ScriptedRunner(ToolCall(tool, "{\"execution_id\":\"missing-task\"}"), FinalStep("done")));
        using var client = factory.CreateAuthenticatedClient();
        try
        {
            var session = await CreateSessionAsync(client);
            var rules = new SqlitePolicyRuleStore(factory.Services.GetRequiredService<HarnessSqliteOptions>(),
                factory.Services.GetRequiredService<IHarnessKeyStore>());
            await rules.CreateAsync(new PolicyRule(new("rule_" + new string('c', 32)), PolicyRuleScope.Global,
                PolicyDecision.Ask, new PolicyMatcher(ToolId: tool), 1, null), null, null, DateTimeOffset.UtcNow, default);
            var run = await CreateRunAndTrustAsync(client, session, workspace, "approve lifecycle");
            var id = run.GetProperty("run_id").GetString()!;
            var approval = await WaitForApprovalIdAsync(client, id, StateDeadline);
            Assert.NotNull(approval);
            using var always = await client.PostAsJsonAsync($"/agent/v1/runs/{id}/approvals/{approval}", new { command = "allow_always" });
            Assert.Equal(HttpStatusCode.BadRequest, always.StatusCode);
            using var result = await client.PostAsJsonAsync($"/agent/v1/runs/{id}/approvals/{approval}", new { command = "allow_once" });
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        }
        finally
        {
            factory.Dispose();
            SqliteConnectionClear(paths);
            Directory.Delete(Path.GetDirectoryName(workspace)!, true);
        }
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("write_file")]
    [InlineData("powershell")]
    [InlineData("run_process")]
    public async Task Coding_identity_survives_public_approval_and_executes_once(string tool)
    {
        var workspace = CreateTempWorkspace(out var paths);
        var external = Path.Combine(Path.GetTempPath(), "ru-coding-approval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        var target = Path.Combine(external, "target.txt");
        if (tool == "read_file") File.WriteAllText(target, "approved-content");
        var arguments = tool switch
        {
            "read_file" => JsonSerializer.Serialize(new { path = target }),
            "write_file" => JsonSerializer.Serialize(new { path = target, content = "approved-content" }),
            "run_process" => JsonSerializer.Serialize(new { executable = "cmd.exe", arguments = new[] { "/d", "/c", "echo", "approved-content" }, working_directory = external }),
            _ => JsonSerializer.Serialize(new { script = "Write-Output approved-content", working_directory = external }),
        };
        using var factory = new BackendWebApplicationFactory(dataPaths: paths,
            modelStepRunner: new ScriptedRunner(ToolCall(tool, arguments), FinalStep("done")));
        using var client = factory.CreateAuthenticatedClient();
        try
        {
            var session = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, session, workspace, "coding approval");
            var runId = created.GetProperty("run_id").GetString()!;
            var approval = await WaitForApprovalIdAsync(client, runId, StateDeadline);
            Assert.NotNull(approval);
            using var response = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approval}",
                new { command = "allow_once" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(await WaitForStatusAsync(client, runId, new HashSet<string> { "completed" }, StateDeadline));
            var events = await ReadEventsAsync(client, runId);
            Assert.Single(events, item => item.GetProperty("event_type").GetString() == "tool.execution_completed");
            if (tool == "write_file") Assert.Equal("approved-content", await File.ReadAllTextAsync(target));
        }
        finally
        {
            factory.Dispose();
            SqliteConnectionClear(paths);
            Directory.Delete(external, true);
            Directory.Delete(Path.GetDirectoryName(workspace)!, true);
        }
    }
}
