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

        var result = await service.ExecuteAuthenticatedV2Async(
            Envelope("search", "web_search", new JsonObject { ["query"] = "ru" }), CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Denied, result.Status);
        Assert.Equal("scope_missing", result.ErrorCode);
        Assert.False(result.AdapterInvoked);
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
    public async Task Service_preserves_fetch_paging_arguments()
    {
        using var fixture = SessionFixture.Create();
        fixture.Session.Open("web-session", new HashSet<string>([RuAgent.Broker.Policy.ToolScope.WorkspaceRead, RuAgent.Broker.Policy.ToolScope.SystemRead, RuAgent.Broker.Policy.ToolScope.WebFetch]));
        using var http = new HttpClient(new Handler());
        await using var service = new ToolExecutionService(fixture.Session, fixture.Root,
            webOptions: new WebOptions(true), webHttpClient: http);
        var result = await service.ExecuteAuthenticatedV2Async(Envelope("page", "web_fetch",
            new JsonObject { ["url"] = "https://93.184.216.34/page", ["offset"] = 8, ["limit"] = 7 }), CancellationToken.None);
        Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
        Assert.Contains("content: looking", result.Output["content"]!.GetValue<string>());
        Assert.Equal(8, result.Output["offset"]!.GetValue<int>());
        Assert.Equal(15, result.Output["next_offset"]!.GetValue<int>());
        Assert.True(result.Output["truncated"]!.GetValue<bool>());
    }

    private static AgentEnvelope Envelope(string requestId, string toolId, JsonObject arguments) =>
        new(AgentEnvelopeCodec.ProtocolVersionV2, requestId, AgentMessageTypes.ToolExecute, "web-session", null, 30_000, new JsonObject
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
