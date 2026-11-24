using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatSessionTurnReplaySummaryTests
{
    private const string SessionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RunId = "run_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task Terminal_summary_reuses_metadata_without_an_individual_run_read()
    {
        using var handler = new ReplayHandler(Summary());
        using var http = new HttpClient(handler);
        var session = Session();
        var summary = Summary();

        var turns = await ChatSessionTurnReplay.LoadAsync(new SessionApiClient(session, http),
            new AgentApiClient(session, http), SessionId, CancellationToken.None, [summary]);

        var turn = Assert.Single(turns!.Value);
        Assert.Same(summary, turn.Run);
        Assert.Equal(0, handler.RunReads);
        Assert.Equal(1, handler.ActivityReads);
        Assert.Equal("Saved narration", Assert.Single(turn.Activity!.Transcript).Text);
        Assert.Equal("Saved answer", Assert.Single(turn.Replies));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_nonterminal_summary_refreshes_the_run_and_keeps_its_completed_reply(bool nonterminal)
    {
        var completed = Summary();
        var known = nonterminal
            ? new[] { completed with { Status = "running", TerminalAt = null } }
            : Array.Empty<GetAgentRunResponse>();
        using var handler = new ReplayHandler(completed);
        using var http = new HttpClient(handler);
        var session = Session();

        var turns = await ChatSessionTurnReplay.LoadAsync(new SessionApiClient(session, http),
            new AgentApiClient(session, http), SessionId, CancellationToken.None, known);

        var turn = Assert.Single(turns!.Value);
        Assert.Equal(1, handler.RunReads);
        Assert.Equal(1, handler.ActivityReads);
        Assert.Equal("completed", turn.Run!.Status);
        Assert.NotNull(turn.Run.TerminalAt);
        Assert.Equal("Saved answer", Assert.Single(turn.Replies));
        Assert.Equal("Saved narration", Assert.Single(turn.Activity!.Transcript).Text);
    }

    [Fact]
    public async Task Wrong_session_summary_cannot_supply_an_outcome_or_activity_to_the_target_history()
    {
        var foreign = Summary() with { SessionId = "cccccccccccccccccccccccccccccccc" };
        using var handler = new ReplayHandler(foreign);
        using var http = new HttpClient(handler);
        var session = Session();

        var turns = await ChatSessionTurnReplay.LoadAsync(new SessionApiClient(session, http),
            new AgentApiClient(session, http), SessionId, CancellationToken.None, [foreign]);

        var turn = Assert.Single(turns!.Value);
        Assert.Equal(1, handler.RunReads);
        Assert.Equal(0, handler.ActivityReads);
        Assert.Null(turn.Run);
        Assert.Null(turn.Activity);
        Assert.Equal("Saved answer", Assert.Single(turn.Replies));
    }

    private static GetAgentRunResponse Summary() => new(RunId, SessionId, "ws_test", "completed", null,
        "standard", 1, new string('a', 64), 2, 4,
        "2026-11-24T00:00:00Z", "2026-11-24T00:00:01Z", "2026-11-24T00:00:01Z", null);

    private static PublicHostSession Session()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return PublicHostSession.FromDescriptor(new RuntimeDescriptor(1, "http://127.0.0.1:8000/",
            "bearer-" + new string('a', 64), process.Id, process.StartTime.ToUniversalTime().ToString("O"),
            new string('a', 32), DateTimeOffset.UtcNow.ToString("O")));
    }

    private sealed class ReplayHandler(GetAgentRunResponse run) : HttpMessageHandler
    {
        internal int RunReads { get; private set; }
        internal int ActivityReads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            object body;
            switch (request.RequestUri!.AbsolutePath)
            {
                case $"/sessions/{SessionId}/messages":
                    body = new
                    {
                        status = "ok",
                        messages = new[]
                        {
                            new { role = "user", content = "Saved request", source_run_id = RunId },
                            new { role = "assistant", content = "Saved answer", source_run_id = RunId },
                        },
                    };
                    break;
                case $"/agent/v1/runs/{RunId}":
                    RunReads++;
                    body = run;
                    break;
                case $"/agent/v1/runs/{RunId}/activities":
                    ActivityReads++;
                    body = new
                    {
                        activities = Array.Empty<object>(),
                        transcript = new[] { new { kind = "text", sequence = 1, text = "Saved narration" } },
                    };
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected replay request: {request.RequestUri.AbsolutePath}");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
        }
    }
}
