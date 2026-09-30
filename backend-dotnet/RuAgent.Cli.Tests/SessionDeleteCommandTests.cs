using System.Net;
using System.Text;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// `agent session delete`: the parser cases, the confirmation gate, and the bounded
/// output around the DELETE call (including the backend's partial-cleanup envelope).
/// </summary>
public sealed class SessionDeleteCommandTests
{
    private static PublicHostSession Session()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuAgent.Protocol.Harness.RuntimeDescriptor(
            1,
            "http://127.0.0.1:8000/",
            "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            proc.Id,
            proc.StartTime.ToUniversalTime().ToString("O"),
            new string('a', 32),
            DateTimeOffset.UtcNow.ToString("O"));
        return PublicHostSession.FromDescriptor(desc);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public void Parser_takes_the_session_id_and_an_optional_yes()
    {
        var bare = AgentCliParser.Parse(["agent", "session", "delete", "sess_1"]);
        Assert.True(bare.Success);
        var bareCommand = Assert.IsType<SessionDeleteCommand>(bare.Command);
        Assert.Equal("sess_1", bareCommand.SessionId);
        Assert.False(bareCommand.Yes);

        var yes = AgentCliParser.Parse(["session", "delete", "sess_1", "--yes"]);
        Assert.True(yes.Success);
        Assert.True(Assert.IsType<SessionDeleteCommand>(yes.Command).Yes);
    }

    [Fact]
    public void Parser_names_what_delete_is_missing_or_given_wrong()
    {
        var missing = AgentCliParser.Parse(["session", "delete"]);
        Assert.False(missing.Success);
        Assert.Equal("missing_session_id", missing.ErrorCode);

        var duplicate = AgentCliParser.Parse(["session", "delete", "sess_1", "--yes", "--yes"]);
        Assert.False(duplicate.Success);
        Assert.Equal("duplicate_switch", duplicate.ErrorCode);

        var invalid = AgentCliParser.Parse(["session", "delete", "bad id"]);
        Assert.False(invalid.Success);
        Assert.Equal("invalid_session_id", invalid.ErrorCode);

        var unknown = AgentCliParser.Parse(["session", "delete", "sess_1", "--force"]);
        Assert.False(unknown.Success);
        Assert.Equal("unknown_switch", unknown.ErrorCode);
    }

    [Fact]
    public async Task Delete_with_yes_issues_the_delete_and_reports_it()
    {
        var deletes = new List<string>();
        var handler = new FakeHandler(req =>
        {
            if (req.Method == HttpMethod.Delete)
            {
                deletes.Add(req.RequestUri!.AbsolutePath);
                return Json("{\"status\":\"ok\"}");
            }

            return Json("{}");
        });
        var outWriter = new StringWriter();
        var commands = new AgentCommands(
            Session(),
            new HttpClient(handler),
            outWriter,
            new StringWriter(),
            new StringReader(string.Empty));

        var exit = await commands.SessionDeleteAsync(
            new SessionDeleteCommand("sess_1", Yes: true),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("/sessions/sess_1", Assert.Single(deletes));
        Assert.Contains("Deleted session sess_1", outWriter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_without_confirmation_never_calls_the_backend()
    {
        var deletes = 0;
        var handler = new FakeHandler(req =>
        {
            if (req.Method == HttpMethod.Delete)
            {
                deletes++;
            }

            return Json("{\"status\":\"ok\"}");
        });
        var errWriter = new StringWriter();
        var commands = new AgentCommands(
            Session(),
            new HttpClient(handler),
            new StringWriter(),
            errWriter,
            new StringReader("n\n"));

        var exit = await commands.SessionDeleteAsync(
            new SessionDeleteCommand("sess_1", Yes: false),
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(0, deletes);
        Assert.Contains("cancelled", errWriter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_partial_cleanup_surfaces_the_backend_message()
    {
        var handler = new FakeHandler(req => req.Method == HttpMethod.Delete
            ? Json("{\"status\":\"error\",\"message\":\"Session đã xoá nhưng artifacts còn sót, hãy thử xoá lại.\"}")
            : Json("{}"));
        var errWriter = new StringWriter();
        var commands = new AgentCommands(
            Session(),
            new HttpClient(handler),
            new StringWriter(),
            errWriter,
            new StringReader(string.Empty));

        var exit = await commands.SessionDeleteAsync(
            new SessionDeleteCommand("sess_1", Yes: true),
            CancellationToken.None);

        Assert.Equal(1, exit);
        var text = errWriter.ToString();
        Assert.Contains("session_delete_failed", text, StringComparison.Ordinal);
        Assert.Contains("artifacts", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", text, StringComparison.Ordinal);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _func;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = func;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_func(request));
    }
}
