using System.Net;
using System.Text.Json.Nodes;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class WebExecutorTests
{
    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    public async Task Http_failures_report_status_instead_of_generic_adapter_error(int status)
    {
        using var fixture = SessionFixture.Create();
        fixture.Session.Open("web-session", new HashSet<string>([RuAgent.Broker.Policy.ToolScope.WebFetch]));
        using var http = new HttpClient(new StatusHandler(status));
        using var service = new ToolExecutionService(fixture.Session, fixture.Root,
            webOptions: new WebOptions(true), webHttpClient: http);
        var pending = await service.ExecuteAsync(Envelope("failed-page", "web_fetch",
            new JsonObject { ["url"] = "https://93.184.216.34/page" }), default);
        var result = await service.RespondToApprovalAsync(new AgentEnvelope(1, "approval", AgentMessageTypes.ApprovalResponse,
            "web-session", null, 30_000, new JsonObject { ["approval_id"] = pending.Output["approval_id"]!.GetValue<string>(), ["approved"] = true }), default);
        Assert.Equal(ToolResultStatuses.Failed, result.Status);
        Assert.Equal("web_http_" + status, result.ErrorCode);
        Assert.Contains("another relevant public source", result.SafeSummary);
    }

    private sealed class StatusHandler(int status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
    }
}
