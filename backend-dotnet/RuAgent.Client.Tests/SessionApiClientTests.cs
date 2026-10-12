using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class SessionApiClientTests
{
    private static PublicHostSession CreateSession(string bearer = "test-bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuAgent.Protocol.Harness.RuntimeDescriptor(1, "http://127.0.0.1:8000/", bearer, proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        return PublicHostSession.FromDescriptor(desc);
    }

    [Fact]
    public async Task Cli_client_marks_session_creation_requests()
    {
        var handler = new FakeHandler(request =>
        {
            Assert.Equal("cli", Assert.Single(request.Headers.GetValues("X-Ru-Client-Source")));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"ok\",\"session_id\":\"test_cli\"}", Encoding.UTF8, "application/json"),
            };
        });
        var client = new SessionApiClient(CreateSession(), new HttpClient(handler), clientSource: "cli");
        Assert.Equal("test_cli", await client.CreateAsync());
    }

    [Fact]
    public async Task CreateAsyncSucceedsWithValidSessionId()
    {
        var bearer = new string('b', 64);
        var session = CreateSession(bearer);
        var handler = new FakeHandler(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.EndsWith("/sessions/new", req.RequestUri!.ToString(), StringComparison.Ordinal);
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal(bearer, req.Headers.Authorization?.Parameter);
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"ok\",\"session_id\":\"abc123\"}", Encoding.UTF8, "application/json")
            };
            return resp;
        });
        var client = new SessionApiClient(session, new HttpClient(handler));
        var id = await client.CreateAsync();
        Assert.Equal("abc123", id);
    }

    [Fact]
    public async Task CreateAsyncRejectsWrongBearer401()
    {
        var session = CreateSession("correct-bearer-" + new string('c', 50));
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error_code\":\"unauthorized\"}") });
        var client = new SessionApiClient(session, new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<AgentApiException>(() => client.CreateAsync());
        Assert.Equal("unauthorized", ex.ErrorCode);
    }

    [Fact]
    public async Task CreateAsyncRejectsNonOkStatus()
    {
        var session = CreateSession();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"error\",\"message\":\"fail\"}") });
        var client = new SessionApiClient(session, new HttpClient(handler));
        await Assert.ThrowsAsync<AgentApiException>(() => client.CreateAsync());
    }

    [Fact]
    public async Task CreateAsyncRejectsInvalidSessionId()
    {
        var session = CreateSession();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\",\"session_id\":\"bad id with spaces\"}") });
        var client = new SessionApiClient(session, new HttpClient(handler));
        await Assert.ThrowsAsync<AgentApiException>(() => client.CreateAsync());
    }

    [Fact]
    public void ParseRejectsUnknownFields()
    {
        Assert.Throws<AgentApiException>(() => SessionApiClient.ParseSessionId("{\"status\":\"ok\",\"session_id\":\"valid\",\"unknown\":\"field\"}"));
    }

    [Fact]
    public void ParseRejectsUnicodeLettersOutsideTheAsciiContract()
    {
        Assert.Throws<AgentApiException>(() => SessionApiClient.ParseSessionId(
            "{\"status\":\"ok\",\"session_id\":\"session-é\"}"));
    }

    [Fact]
    public async Task DeleteAsyncIssuesDeleteToTheSessionPath()
    {
        var session = CreateSession();
        HttpRequestMessage? seen = null;
        var handler = new FakeHandler(req =>
        {
            seen = req;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\"}") };
        });
        var client = new SessionApiClient(session, new HttpClient(handler));

        await client.DeleteAsync("sess_1");

        Assert.Equal(HttpMethod.Delete, seen!.Method);
        Assert.EndsWith("/sessions/sess_1", seen.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteAsyncSurfacesPartialCleanupInsteadOfTreatingHttp200AsSuccess()
    {
        var session = CreateSession();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"status\":\"error\",\"message\":\"Session đã xoá nhưng artifacts còn sót, hãy thử xoá lại.\"}"),
        });
        var client = new SessionApiClient(session, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<AgentApiException>(() => client.DeleteAsync("sess_1"));

        Assert.Contains("artifacts", ex.Message, StringComparison.Ordinal);
        Assert.Equal("session_delete_failed", ex.ErrorCode);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _func;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = func;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_func(request));
    }
}
