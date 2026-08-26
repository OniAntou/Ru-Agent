using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ReconcileCommandTests
{
    private const string RunId = "run_11111111111111111111111111111111";
    private static readonly string FreshHash = new string('a', 64);

    // ------------------------------------------------------------------
    // Parser.
    // ------------------------------------------------------------------

    [Fact]
    public void Parser_accepts_run_id_with_interactive_outcome()
    {
        var result = AgentCliParser.Parse(new[] { "agent", "reconcile", RunId, "--outcome", "applied" });

        Assert.True(result.Success);
        var command = Assert.IsType<ReconcileCommand>(result.Command);
        Assert.Equal(RunId, command.RunId);
        Assert.Equal("applied", command.OutcomeToken);
        Assert.Null(command.SnapshotHash);
    }

    [Theory]
    [InlineData("not-applied", "not_applied")]
    [InlineData("unknown", "unknown_continue")]
    public void Parser_maps_cli_outcome_tokens_to_wire_tokens(string cliToken, string wireToken)
    {
        var result = AgentCliParser.Parse(new[] { "agent", "reconcile", RunId, "--outcome", cliToken });

        Assert.True(result.Success);
        var command = Assert.IsType<ReconcileCommand>(result.Command);
        Assert.Equal(wireToken, command.ToWireOutcome());
    }

    [Fact]
    public void Parser_rejects_unknown_outcome_tokens()
    {
        var result = AgentCliParser.Parse(new[] { "agent", "reconcile", RunId, "--outcome", "sort_of" });

        Assert.False(result.Success);
        Assert.Equal("invalid_outcome", result.ErrorCode);
    }

    [Fact]
    public void Parser_validates_the_snapshot_hash_format()
    {
        var bad = AgentCliParser.Parse(new[] { "agent", "reconcile", RunId, "--hash", "nothex" });
        var good = AgentCliParser.Parse(new[] { "agent", "reconcile", RunId, "--hash", FreshHash });

        Assert.False(bad.Success);
        Assert.Equal("invalid_snapshot_hash", bad.ErrorCode);
        Assert.True(good.Success);
        Assert.Equal(FreshHash, Assert.IsType<ReconcileCommand>(good.Command).SnapshotHash);
    }

    [Fact]
    public void Parser_requires_a_run_id()
    {
        var result = AgentCliParser.Parse(new[] { "agent", "reconcile" });

        Assert.False(result.Success);
        Assert.Equal("missing_run_id", result.ErrorCode);
    }

    // ------------------------------------------------------------------
    // Command execution against a scripted host.
    // ------------------------------------------------------------------

    private static PublicHostSession Session() =>
        new(new Uri("http://127.0.0.1:5000/"), "test-token", new RuntimeDescriptor(
            ProtocolVersion: 1,
            BaseUrl: "http://127.0.0.1:5000/",
            BearerToken: "test-token",
            ProcessId: 1,
            ProcessStartTime: "2026-08-26T00:00:00Z",
            InstanceNonce: "nonce",
            CreatedAt: "2026-08-26T00:00:00Z"));

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly List<(string Path, string Body)> _requests = new();
        private readonly Func<string, string, HttpResponseMessage> _responder;

        public ScriptedHandler(Func<string, string, HttpResponseMessage> responder) => _responder = responder;

        public IReadOnlyList<(string Path, string Body)> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            _requests.Add((request.RequestUri!.PathAndQuery, body));
            return Task.FromResult(_responder(request.RequestUri.PathAndQuery, body));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object payload)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }

    [Fact]
    public async Task Interactive_reconcile_fetches_info_then_applies_with_the_fresh_hash()
    {
        var dummy = new string('0', 64);
        var handler = new ScriptedHandler((path, body) =>
        {
            if (body.Contains(FreshHash))
            {
                return Json(HttpStatusCode.OK, new { run_id = RunId, state = "running", resolution = "applied", intent_code = "recovery_ambiguous" });
            }

            if (body.Contains(dummy))
            {
                return Json(HttpStatusCode.Conflict, new { error_code = "reconcile_stale", snapshot_hash = FreshHash, intent_code = "recovery_ambiguous" });
            }

            return Json(HttpStatusCode.BadRequest, new
            {
                error_code = "invalid_snapshot_hash",
                snapshot_hash = FreshHash,
                intent_code = "recovery_ambiguous",
            });
        });
        using var http = new HttpClient(handler);
        using var input = new StringReader("a\n");
        using var output = new StringWriter();
        using var err = new StringWriter();
        var commands = new AgentCommands(Session(), http, output, err, input);

        var parseResult = AgentCliParser.Parse(new[] { "agent", "reconcile", RunId });
        var exit = await commands.ReconcileAsync(
            Assert.IsType<ReconcileCommand>(parseResult.Command!), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(FreshHash, handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"applied\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("recovery_ambiguous", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(err.ToString());
    }

    [Fact]
    public async Task Json_mode_prints_info_and_never_prompts()
    {
        var dummy = new string('0', 64);
        var handler = new ScriptedHandler((path, body) =>
        {
            if (body.Contains(dummy))
            {
                return Json(HttpStatusCode.Conflict, new { error_code = "reconcile_stale", snapshot_hash = FreshHash, intent_code = "recovery_ambiguous" });
            }

            return Json(HttpStatusCode.BadRequest, new
            {
                error_code = "invalid_snapshot_hash",
                snapshot_hash = FreshHash,
                intent_code = "recovery_ambiguous",
            });
        });
        using var http = new HttpClient(handler);
        using var input = new StringReader("this must never be read\n");
        using var output = new StringWriter();
        var commands = new AgentCommands(Session(), http, output, new StringWriter(), input);

        var exit = await commands.ReconcileAsync(
            new ReconcileCommand(RunId, OutcomeToken: null, SnapshotHash: null, Json: true),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Single(handler.Requests);
        var printed = JsonSerializer.Deserialize<JsonElement>(output.ToString());
        Assert.Equal("recovery_ambiguous", printed.GetProperty("intent_code").GetString());
        Assert.Equal(FreshHash, printed.GetProperty("snapshot_hash").GetString());
    }

    [Fact]
    public async Task Json_mode_with_an_outcome_but_no_hash_fails_without_prompting()
    {
        var handler = new ScriptedHandler((path, body) => Json(HttpStatusCode.OK, new { }));
        using var http = new HttpClient(handler);
        using var input = new StringReader("never read\n");
        var commands = new AgentCommands(Session(), http, new StringWriter(), new StringWriter(), input);

        var exit = await commands.ReconcileAsync(
            new ReconcileCommand(RunId, OutcomeToken: "applied", SnapshotHash: null, Json: true),
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Stale_hash_reports_reconcile_stale_and_exits_nonzero()
    {
        var handler = new ScriptedHandler((path, body) =>
            Json(HttpStatusCode.Conflict, new { error_code = "reconcile_stale", snapshot_hash = FreshHash }));
        using var http = new HttpClient(handler);
        var commands = new AgentCommands(Session(), http, new StringWriter(), new StringWriter(), new StringReader(string.Empty));

        var exit = await commands.ReconcileAsync(
            new ReconcileCommand(RunId, OutcomeToken: "applied", SnapshotHash: FreshHash, Json: false),
            CancellationToken.None);

        Assert.Equal(1, exit);
    }
}
