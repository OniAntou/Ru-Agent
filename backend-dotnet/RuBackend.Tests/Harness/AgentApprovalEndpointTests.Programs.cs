using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class AgentApprovalEndpointTests
{
    [Fact]
    public async Task PublicRunUsesComposedProgramJournalAndPreservesNestedReceipts()
    {
        var workspace = CreateTempWorkspace(out var paths);
        const string code = "tools.write_file({path:'created.txt',content:'program-data'}); return tools.read_file({path:'created.txt'}).output.content;";
        using var factory = new BackendWebApplicationFactory(dataPaths: paths,
            modelStepRunner: new ScriptedRunner(ToolCall("run_code", JsonSerializer.Serialize(new { code })), FinalStep("done")));
        using var client = factory.CreateAuthenticatedClient();
        try
        {
            var session = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, session, workspace, "program integration");
            var runId = created.GetProperty("run_id").GetString()!;
            Assert.True(await WaitForStatusAsync(client, runId, new HashSet<string> { "completed" }, StateDeadline));
            Assert.Equal("program-data", await File.ReadAllTextAsync(Path.Combine(workspace, "created.txt")));
            var store = factory.Services.GetRequiredService<HarnessPayloadStore>();
            var ids = await store.ListPayloadIdsAsync(new RunId(runId), HarnessPayloadKind.ToolOutput, default);
            var found = false;
            foreach (var id in ids)
            {
                var payload = await store.ReadForClientAsync(new RunId(runId), id, default);
                using var json = JsonDocument.Parse(payload.Plaintext);
                if (!json.RootElement.TryGetProperty("nested_receipts", out var trace)) continue;
                Assert.Equal(2, trace.GetArrayLength());
                Assert.Equal("program-data", json.RootElement.GetProperty("result").GetString());
                found = true;
            }
            Assert.True(found);
        }
        finally
        {
            factory.Dispose();
            SqliteConnectionClear(paths);
            Directory.Delete(Path.GetDirectoryName(workspace)!, true);
        }
    }
}
