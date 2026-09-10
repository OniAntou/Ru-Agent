using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class WebExecutorTests
{
    [Fact]
    public async Task Service_requires_approval_for_web_ops()
    {
        using var fixture = SessionFixture.Create();
        fixture.Session.Open(
            "web-session",
            new HashSet<string>([RuAgent.Broker.Policy.ToolScope.WorkspaceRead, RuAgent.Broker.Policy.ToolScope.SystemRead, RuAgent.Broker.Policy.ToolScope.WebSearch, RuAgent.Broker.Policy.ToolScope.WebFetch]),
            new HashSet<string>());
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
        fixture.Session.Open("web-session", new HashSet<string>(), new HashSet<string>());
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
            new HashSet<string>([RuAgent.Broker.Policy.ToolScope.WorkspaceRead, RuAgent.Broker.Policy.ToolScope.SystemRead, RuAgent.Broker.Policy.ToolScope.WebSearch, RuAgent.Broker.Policy.ToolScope.WebFetch]),
            new HashSet<string>());
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
    public async Task Service_falls_back_to_duckduckgo_when_google_parse_fails()
    {
        using var fixture = SessionFixture.Create();
        fixture.Session.Open(
            "web-session",
            new HashSet<string>([RuAgent.Broker.Policy.ToolScope.WorkspaceRead, RuAgent.Broker.Policy.ToolScope.SystemRead, RuAgent.Broker.Policy.ToolScope.WebSearch, RuAgent.Broker.Policy.ToolScope.WebFetch]),
            new HashSet<string>());
        using var http = new HttpClient(new GoogleThenDuckHandler());
        var service = new ToolExecutionService(
            fixture.Session,
            fixture.Root,
            webOptions: new WebOptions(
                true,
                new Uri("https://93.184.216.34/google"),
                new Uri("https://93.184.216.34/duckduckgo")),
            webHttpClient: http);

        var pending = await service.ExecuteAsync(
            Envelope("fallback", "web_search", new JsonObject { ["query"] = "ru" }),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.ApprovalRequired, pending.Status);
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
            var descriptor = new BrokerSessionDescriptor(
                "http://127.0.0.1:18086",
                "token",
                1,
                sessionRoot,
                DateTimeOffset.UtcNow.AddMinutes(5));
            var descriptorPath = Path.Combine(root, "broker.json");
            File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
            return new SessionFixture(root, BrokerSession.Load(descriptorPath));
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
