using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuBackend.Harness.Broker;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// The Host's pre-commit check on an allow: the Broker must still hold the approval as pending. These cases run
/// on the real host and the real Broker; the Broker's side is changed directly through the gateway factory, the
/// way a Broker that resolved or lost the approval on its own would look to the Host.
/// </summary>
public sealed partial class AgentApprovalEndpointTests
{
    [Fact]
    public async Task Approval_BrokerNoLongerHoldsIt_IsStaleBeforeAnyCommit()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var (fact, client, runId, approvalId) = await StartWaitingApprovalRunAsync(wsPath, dataPaths);
            factory = fact;
            var eventsBefore = await ReadEventsAsync(client, runId);

            // The Broker resolves the approval on its own side. The Host still records the run as waiting on it.
            var gateways = factory.Services.GetRequiredService<IToolGatewayFactory>();
            var brokerResolution = await gateways.RespondApprovalAsync(runId, approvalId, "deny", CancellationToken.None);
            Assert.False(brokerResolution.Authorized);
            Assert.False((await gateways.ProbeApprovalAsync(runId, approvalId, CancellationToken.None)).Known);

            foreach (var body in new object[] { new { decision = "allow" }, new { command = "allow_always" } })
            {
                using var resp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", body);
                Assert.Equal((HttpStatusCode)409, resp.StatusCode);
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("approval_stale", json.GetProperty("error_code").GetString());
            }

            // Refused before the commit: the run still waits, and its durable stream gained no event.
            Assert.Equal("waiting_approval", await GetStatusAsync(client, runId));
            var eventsAfter = await ReadEventsAsync(client, runId);
            Assert.Equal(eventsBefore.Count, eventsAfter.Count);
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    [Fact]
    public async Task Approval_KnownToBroker_StillProceedsToAuthorized()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var (fact, client, runId, approvalId) = await StartWaitingApprovalRunAsync(wsPath, dataPaths);
            factory = fact;
            var gateways = factory.Services.GetRequiredService<IToolGatewayFactory>();
            Assert.True((await gateways.ProbeApprovalAsync(runId, approvalId, CancellationToken.None)).Known);

            using var resp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { decision = "allow" });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("authorized", json.GetProperty("status").GetString());
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }
}
