using System.Net;
using System.Text;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class SessionApiClientHistoryTests
{
    [Fact]
    public async Task GetMessagePageAsync_escapes_session_and_cursor_and_returns_message_page()
    {
        HttpRequestMessage? seen = null;
        var handler = new FakeHandler(request =>
        {
            seen = request;
            return Json("""{"status":"ok","session_id":"session-1","messages":[{"role":"user","content":"hello"}],"start_index":0,"total_messages":1,"next_cursor":null,"history_epoch":1}""");
        });
        var client = new SessionApiClient(SessionApiClientTestsAccessor.CreateSession(), new HttpClient(handler));

        var result = await client.GetMessagePageAsync("session-1", "h1.cursor/+", 25);

        Assert.Equal(HttpMethod.Get, seen!.Method);
        Assert.Equal("Bearer", seen.Headers.Authorization?.Scheme);
        Assert.EndsWith("/sessions/session-1/messages", seen.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("cursor=h1.cursor%2F%2B", seen.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("limit=25", seen.RequestUri.Query, StringComparison.Ordinal);
        Assert.Equal("hello", result.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ListPageAsync_encodes_filters_and_sends_bearer()
    {
        HttpRequestMessage? seen = null;
        var handler = new FakeHandler(request =>
        {
            seen = request;
            return Json("""{"status":"ok","sessions":[],"next_cursor":"h1.next","sessions_truncated":true}""");
        });
        var client = new SessionApiClient(SessionApiClientTestsAccessor.CreateSession(), new HttpClient(handler));

        var result = await client.ListPageAsync("h1.cursor/+=", 25, "  snow & sun  ");

        Assert.Equal(HttpMethod.Get, seen!.Method);
        Assert.Equal("Bearer", seen.Headers.Authorization?.Scheme);
        Assert.EndsWith("/sessions/page", seen.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("cursor=h1.cursor%2F%2B%3D", seen.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("limit=25", seen.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("query=snow%20%26%20sun", seen.RequestUri.Query, StringComparison.Ordinal);
        Assert.Equal("h1.next", result.GetProperty("next_cursor").GetString());
    }

    [Fact]
    public async Task SearchMessagesAsync_calls_search_route_with_bounded_query_and_limit()
    {
        HttpRequestMessage? seen = null;
        var handler = new FakeHandler(request =>
        {
            seen = request;
            return Json("""{"status":"ok","hits":[{"session_id":"abc","message_index":2,"role":"user","snippet":"match"}]}""");
        });
        var client = new SessionApiClient(SessionApiClientTestsAccessor.CreateSession(), new HttpClient(handler));

        var result = await client.SearchMessagesAsync("word with spaces", 12);

        Assert.EndsWith("/sessions/search", seen!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("query=word%20with%20spaces", seen.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("limit=12", seen.RequestUri.Query, StringComparison.Ordinal);
        Assert.Equal(2, result.GetProperty("hits")[0].GetProperty("message_index").GetInt32());
    }

    [Fact]
    public async Task SearchMessagesAsync_preserves_server_history_error_code()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"status":"error","code":"session_history_query_invalid"}""", Encoding.UTF8, "application/json"),
        });
        var client = new SessionApiClient(SessionApiClientTestsAccessor.CreateSession(), new HttpClient(handler));

        var error = await Assert.ThrowsAsync<AgentApiException>(
            () => client.SearchMessagesAsync("valid"));

        Assert.Equal("session_history_query_invalid", error.ErrorCode);
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public async Task History_methods_reject_oversized_or_controlled_arguments_before_sending()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("request should not be sent"));
        var client = new SessionApiClient(SessionApiClientTestsAccessor.CreateSession(), new HttpClient(handler));

        var listError = await Assert.ThrowsAsync<AgentApiException>(
            () => client.ListPageAsync(new string('x', 2049)));
        var queryError = await Assert.ThrowsAsync<AgentApiException>(
            () => client.SearchMessagesAsync("bad\nquery"));
        var sessionError = await Assert.ThrowsAsync<AgentApiException>(
            () => client.GetMessagePageAsync("bad/id"));

        Assert.Equal("session_history_arguments_invalid", listError.ErrorCode);
        Assert.Equal("session_search_arguments_invalid", queryError.ErrorCode);
        Assert.Equal("session_history_arguments_invalid", sessionError.ErrorCode);
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }

    private static class SessionApiClientTestsAccessor
    {
        public static PublicHostSession CreateSession()
        {
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var descriptor = new RuAgent.Protocol.Harness.RuntimeDescriptor(
                1, "http://127.0.0.1:8000/", "test-bearer-" + new string('a', 64), process.Id,
                process.StartTime.ToUniversalTime().ToString("O"), new string('a', 32),
                DateTimeOffset.UtcNow.ToString("O"));
            return PublicHostSession.FromDescriptor(descriptor);
        }
    }
}
