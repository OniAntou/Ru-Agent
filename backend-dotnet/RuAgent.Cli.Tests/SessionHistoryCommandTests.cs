using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class SessionHistoryCommandTests
{
    [Fact]
    public void Session_history_parser_accepts_cursor_limit_and_json_and_rejects_bad_values()
    {
        var result = AgentCliParser.Parse(
            ["session", "history", "abc-123", "--cursor", "h1.more", "--limit", "25", "--json"]);
        var command = Assert.IsType<SessionHistoryCommand>(result.Command);
        Assert.True(result.Success);
        Assert.Equal("abc-123", command.SessionId);
        Assert.Equal("h1.more", command.Cursor);
        Assert.Equal(25, command.Limit);
        Assert.True(command.Json);

        Assert.Equal("missing_session_id", AgentCliParser.Parse(["session", "history"]).ErrorCode);
        Assert.Equal("invalid_limit", AgentCliParser.Parse(["session", "history", "abc", "--limit", "201"]).ErrorCode);
        Assert.Equal("invalid_cursor", AgentCliParser.Parse(["session", "history", "abc", "--cursor", "bad\nvalue"]).ErrorCode);
    }

    [Fact]
    public void Session_list_parser_accepts_cursor_limit_query_and_json()
    {
        var result = AgentCliParser.Parse(
            ["agent", "session", "list", "--cursor", "h1.abc", "--limit", "25", "--query", "two words", "--json"]);

        var command = Assert.IsType<SessionListCommand>(result.Command);
        Assert.True(result.Success);
        Assert.Equal("h1.abc", command.Cursor);
        Assert.Equal(25, command.Limit);
        Assert.Equal("two words", command.Query);
        Assert.True(command.Json);
    }

    [Fact]
    public void Session_list_parser_rejects_invalid_or_duplicate_page_options()
    {
        Assert.Equal("invalid_limit", AgentCliParser.Parse(["session", "list", "--limit", "0"]).ErrorCode);
        Assert.Equal("duplicate_switch", AgentCliParser.Parse(["session", "list", "--cursor", "h1.a", "--cursor", "h1.b"]).ErrorCode);
        Assert.Equal("invalid_cursor", AgentCliParser.Parse(["session", "list", "--cursor", "bad\nvalue"]).ErrorCode);
    }

    [Fact]
    public void Session_search_parser_joins_query_words_and_rejects_missing_or_invalid_query()
    {
        var result = AgentCliParser.Parse(["session", "search", "several", "words", "--limit", "15"]);
        var command = Assert.IsType<SessionSearchCommand>(result.Command);
        Assert.Equal("several words", command.Query);
        Assert.Equal(15, command.Limit);

        Assert.Equal("missing_query", AgentCliParser.Parse(["session", "search", "--json"]).ErrorCode);
        Assert.Equal("invalid_limit", AgentCliParser.Parse(["session", "search", "word", "--limit", "201"]).ErrorCode);
    }

    [Fact]
    public async Task Session_list_uses_page_parameters_and_prints_next_cursor_without_terminal_controls()
    {
        HttpRequestMessage? seen = null;
        var handler = new FakeHandler(request =>
        {
            seen = request;
            return Json("""{"status":"ok","sessions":[{"id":"session-1","title":"safe\u001b[31m title","message_count":3}],"next_cursor":"h1.next","sessions_truncated":true}""");
        });
        var output = new StringWriter();
        var commands = new AgentCommands(Session(), new HttpClient(handler), output, new StringWriter());

        var result = await commands.SessionListAsync(
            new SessionListCommand(false, Cursor: "h1.start", Limit: 12, Query: "find me"),
            CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("cursor=h1.start", seen!.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("limit=12", seen.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("query=find%20me", seen.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("Next cursor: h1.next", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_search_sanitizes_bounded_text_output_and_wraps_json_envelope()
    {
        var snippet = new string('x', 300) + "\u001b[2J";
        var handler = new FakeHandler(_ => Json(JsonSerializer.Serialize(new
        {
            status = "ok",
            hits = new[] { new { session_id = "session-1", message_index = 4, role = "user", snippet } },
        })));
        var output = new StringWriter();
        var commands = new AgentCommands(Session(), new HttpClient(handler), output, new StringWriter());

        var result = await commands.SessionSearchAsync(new SessionSearchCommand("hello", 10, Json: false), CancellationToken.None);

        Assert.Equal(0, result);
        Assert.DoesNotContain("\u001b", output.ToString(), StringComparison.Ordinal);
        var renderedSnippet = output.ToString().Split("[user] ", StringSplitOptions.None)[1].Trim();
        Assert.Equal(240, renderedSnippet.Length);

        var jsonOutput = new StringWriter();
        var jsonCommands = new AgentCommands(Session(), new HttpClient(handler), jsonOutput, new StringWriter());
        await jsonCommands.SessionSearchAsync(new SessionSearchCommand("hello", 10, Json: true), CancellationToken.None);
        using var document = JsonDocument.Parse(jsonOutput.ToString());
        Assert.Equal("session_search", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Session_history_sends_page_arguments_and_bounds_sanitized_text()
    {
        HttpRequestMessage? seen = null;
        var longMessage = new string('x', 1200) + "\u001b[2J";
        var handler = new FakeHandler(request =>
        {
            seen = request;
            return Json(JsonSerializer.Serialize(new
            {
                status = "ok", session_id = "abc", start_index = 4, total_messages = 8,
                messages = new[] { new { role = "user", content = longMessage } },
                next_cursor = "h1.next",
            }));
        });
        var output = new StringWriter();
        var commands = new AgentCommands(Session(), new HttpClient(handler), output, new StringWriter());

        var result = await commands.SessionHistoryAsync(
            new SessionHistoryCommand("abc", "h1.before", 10, Json: false), CancellationToken.None);

        Assert.Equal(0, result);
        Assert.EndsWith("/sessions/abc/messages", seen!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("cursor=h1.before", seen.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("limit=10", seen.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("Next cursor: h1.next", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1000, output.ToString().Split("[user] ", StringSplitOptions.None)[1]
            .Split(Environment.NewLine, StringSplitOptions.None)[0].Length);
    }

    private static PublicHostSession Session()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var descriptor = new RuAgent.Protocol.Harness.RuntimeDescriptor(
            1, "http://127.0.0.1:8000/", "test-bearer-" + new string('a', 64), process.Id,
            process.StartTime.ToUniversalTime().ToString("O"), new string('b', 32),
            DateTimeOffset.UtcNow.ToString("O"));
        return PublicHostSession.FromDescriptor(descriptor);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
