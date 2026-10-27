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
    [InlineData(403, "refuses automated clients")]
    [InlineData(404, "does not exist")]
    [InlineData(429, "rate limiting")]
    public async Task Http_failures_report_status_instead_of_generic_adapter_error(int status, string hint)
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
        Assert.Contains(hint, result.SafeSummary);
    }

    [Fact]
    public async Task Unreadable_search_results_tell_the_model_to_rephrase_instead_of_a_generic_rejection()
    {
        using var fixture = SessionFixture.Create();
        fixture.Session.Open("web-session", new HashSet<string>([RuAgent.Broker.Policy.ToolScope.WebSearch]));
        // A configured JSON backend keeps the engine race (and its DNS lookups) out of the test.
        using var http = new HttpClient(new JsonBodyHandler("{\"results\":[]}"));
        using var service = new ToolExecutionService(fixture.Session, fixture.Root,
            webOptions: new WebOptions(true, new Uri("https://93.184.216.34/search"), searchMode: WebSearchMode.ConfiguredJson),
            webHttpClient: http);
        var pending = await service.ExecuteAsync(Envelope("search", "web_search",
            new JsonObject { ["query"] = "gold price" }), default);
        var result = pending.Status == ToolResultStatuses.Failed ? pending
            : await service.RespondToApprovalAsync(new AgentEnvelope(1, "approval", AgentMessageTypes.ApprovalResponse,
                "web-session", null, 30_000, new JsonObject { ["approval_id"] = pending.Output["approval_id"]!.GetValue<string>(), ["approved"] = true }), default);
        Assert.Equal("web_search_parse_failed", result.ErrorCode);
        Assert.Contains("shorter keyword query", result.SafeSummary);
    }

    private sealed class JsonBodyHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }

    private sealed class StatusHandler(int status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
    }
}
