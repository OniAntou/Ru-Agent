using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class WebExecutorTests
{
    [Fact]
    public async Task Service_requires_approval_for_web_ops()
    {
        using var fixture = SessionFixture.Create();
        fixture.Session.Open(
            "web-session",
            new HashSet<string>([RuAgent.Broker.Policy.ToolScope.WorkspaceRead, RuAgent.Broker.Policy.ToolScope.SystemRead, RuAgent.Broker.Policy.ToolScope.WebSearch, RuAgent.Broker.Policy.ToolScope.WebFetch]));
        using var http = new HttpClient(new Handler());
        var service = new ToolExecutionService(
            fixture.Session,
            fixture.Root,
            webOptions: new WebOptions(true, new Uri("https://93.184.216.34/search")),
            webHttpClient: http);

        // The no-grant deny path is covered by Service_denies_web_scope_before_http_adapter.
        var search = await service.ExecuteAsync(Envelope(
            "search", "web_search", new JsonObject { ["query"] = "ru" }), CancellationToken.None);
        var fetch = await service.ExecuteAsync(Envelope(
            "fetch", "web_fetch", new JsonObject { ["url"] = "https://93.184.216.34/page" }), CancellationToken.None);

        Assert.Equal(ToolResultStatuses.ApprovalRequired, search.Status);
        Assert.False(search.AdapterInvoked);
        Assert.Equal(ToolResultStatuses.ApprovalRequired, fetch.Status);
        Assert.False(fetch.AdapterInvoked);
    }

    [Fact]
    public async Task Service_denies_web_scope_before_http_adapter()
    {
        using var fixture = SessionFixture.Create();
        fixture.Session.Open("web-session", new HashSet<string>());
        using var http = new HttpClient(new Handler());
        var service = new ToolExecutionService(
            fixture.Session,
            fixture.Root,
            webOptions: new WebOptions(true, new Uri("https://93.184.216.34/search")),
            webHttpClient: http);

        var result = await service.ExecuteAsync(
            Envelope("search", "web_search", new JsonObject { ["query"] = "ru" }), CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Denied, result.Status);
        Assert.Equal("scope_missing", result.ErrorCode);
        Assert.False(result.AdapterInvoked);
    }

    [Fact]
    public async Task Service_gates_json_search_behind_approval()
    {
        using var fixture = SessionFixture.Create();
        fixture.Session.Open(
            "web-session",
            new HashSet<string>([RuAgent.Broker.Policy.ToolScope.WorkspaceRead, RuAgent.Broker.Policy.ToolScope.SystemRead, RuAgent.Broker.Policy.ToolScope.WebSearch, RuAgent.Broker.Policy.ToolScope.WebFetch]));
        using var http = new HttpClient(new JsonHandler());
        var service = new ToolExecutionService(
            fixture.Session,
            fixture.Root,
            webOptions: new WebOptions(true, new Uri("https://93.184.216.34/json-search")),
            webHttpClient: http);

        var result = await service.ExecuteAsync(
            Envelope("json-search", "web_search", new JsonObject { ["query"] = "ru" }),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.ApprovalRequired, result.Status);
        Assert.False(result.AdapterInvoked);
    }

    [Fact]
    public async Task Service_asks_for_approval_on_the_configured_engine_alone()
    {
        using var fixture = SessionFixture.Create();
        fixture.Session.Open(
            "web-session",
            new HashSet<string>([RuAgent.Broker.Policy.ToolScope.WorkspaceRead, RuAgent.Broker.Policy.ToolScope.SystemRead, RuAgent.Broker.Policy.ToolScope.WebSearch, RuAgent.Broker.Policy.ToolScope.WebFetch]));
        using var http = new HttpClient(new GoogleThenDuckHandler());
        var service = new ToolExecutionService(
            fixture.Session,
            fixture.Root,
            webOptions: new WebOptions(
                true,
                new Uri("https://93.184.216.34/google")),
            webHttpClient: http);

        var pending = await service.ExecuteAsync(
            Envelope("fallback", "web_search", new JsonObject { ["query"] = "ru" }),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.ApprovalRequired, pending.Status);
    }

    /// <summary>
    /// Ownership boundary: a web client the service creates itself is disposed
    /// with the service, while an injected client stays owned by its caller and
    /// keeps working after the service is disposed.
    /// </summary>
    [Fact]
    public async Task Service_disposes_only_the_web_client_it_created()
    {
        using var fixture = SessionFixture.Create();

        var handler = new TrackingHandler();
        using var injected = new HttpClient(handler);
        var injectedService = new ToolExecutionService(
            fixture.Session,
            fixture.Root,
            webOptions: new WebOptions(true, new Uri("https://93.184.216.34/search")),
            webHttpClient: injected);
        Assert.Null(injectedService.OwnedWebHttpClientForTest);

        injectedService.Dispose();

        Assert.False(handler.Disposed);
        using var response = await injected.GetAsync(new Uri("https://93.184.216.34/"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var ownedService = new ToolExecutionService(
            fixture.Session,
            fixture.Root,
            webOptions: new WebOptions(true, new Uri("https://93.184.216.34/search")));
        var owned = ownedService.OwnedWebHttpClientForTest;
        Assert.NotNull(owned);

        ownedService.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => owned.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri("https://93.184.216.34/"))));
    }

    [Fact]
    public async Task Service_preserves_fetch_paging_arguments_through_approval()
    {
        using var fixture = SessionFixture.Create();
        fixture.Session.Open("web-session", new HashSet<string>([RuAgent.Broker.Policy.ToolScope.WorkspaceRead, RuAgent.Broker.Policy.ToolScope.SystemRead, RuAgent.Broker.Policy.ToolScope.WebFetch]));
        using var http = new HttpClient(new Handler());
        await using var service = new ToolExecutionService(fixture.Session, fixture.Root,
            webOptions: new WebOptions(true), webHttpClient: http);
        var pending = await service.ExecuteAsync(Envelope("page", "web_fetch",
            new JsonObject { ["url"] = "https://93.184.216.34/page", ["offset"] = 8, ["limit"] = 7 }), CancellationToken.None);
        Assert.Equal(ToolResultStatuses.ApprovalRequired, pending.Status);
        var result = await service.RespondToApprovalAsync(new AgentEnvelope(1, "approval", AgentMessageTypes.ApprovalResponse,
            "web-session", null, 30_000, new JsonObject { ["approval_id"] = pending.Output["approval_id"]!.GetValue<string>(), ["approved"] = true }), CancellationToken.None);
        Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
        Assert.Contains("content: looking", result.Output["content"]!.GetValue<string>());
        Assert.Equal(8, result.Output["offset"]!.GetValue<int>());
        Assert.Equal(15, result.Output["next_offset"]!.GetValue<int>());
        Assert.True(result.Output["truncated"]!.GetValue<bool>());
    }

    private static AgentEnvelope Envelope(string requestId, string toolId, JsonObject arguments) =>
        new(1, requestId, AgentMessageTypes.ToolRequest, "web-session", null, 30_000, new JsonObject
        {
            ["tool_id"] = toolId,
            ["tool_version"] = "1.0.0",
            ["arguments"] = arguments,
        });

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri?.AbsolutePath.EndsWith("search", StringComparison.Ordinal) == true
                ? "<a href=\"https://93.184.216.34/result\">Result</a>"
                : "<script>alert(1)</script><p>Trusted-looking but untrusted content.</p>";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
            return Task.FromResult(response);
        }
    }

    private sealed class JsonHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"results\":[{\"title\":\"JSON result\",\"url\":\"https://93.184.216.34/result\",\"snippet\":\"bounded snippet\"}]}")
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        }
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok"),
            });

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class GoogleThenDuckHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = path.Contains("duckduckgo", StringComparison.Ordinal)
                ? "<a class=\"result__a\" href=\"https://93.184.216.34/result\">Duck result</a>"
                : "<html><body><p>consent page without result anchors</p></body></html>";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
            return Task.FromResult(response);
        }
    }

    private sealed class SessionFixture : IDisposable
    {
        private SessionFixture(string root, BrokerSession session)
        {
            Root = root;
            Session = session;
        }

        public string Root { get; }
        public BrokerSession Session { get; }

        public static SessionFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-web-service-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sessionRoot = Path.Combine(root, "session");
            Directory.CreateDirectory(sessionRoot);
            return new SessionFixture(
                root,
                BrokerSession.CreateForHost(sessionRoot, DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
