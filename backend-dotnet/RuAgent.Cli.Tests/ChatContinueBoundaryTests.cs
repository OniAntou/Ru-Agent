using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatContinueBoundaryTests
{
    [Theory]
    [InlineData("completed", "sess_123", "run_not_runnable")]
    [InlineData("failed", "sess_123", "run_not_runnable")]
    [InlineData("running", "sess_123", "run_not_parked")]
    [InlineData("waiting_approval", "sess_123", "run_not_parked")]
    [InlineData("waiting_user", "sess_foreign", "run_session_mismatch")]
    public async Task TypedContinue_RevalidatesTheRetainedRunBeforeAnyPostOrStream(
        string nextStatus, string nextSession, string expectedError)
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var host = PublicHostSession.FromDescriptor(new RuntimeDescriptor(
            1, "http://127.0.0.1:8000/", "synthetic-continue-boundary-bearer", process.Id,
            process.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O")));
        const string runId = "run_cccccccccccccccccccccccccccccccc";
        var reads = 0;
        var posts = new List<string>();
        var streams = 0;
        using var handler = new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post) posts.Add(path);
            if (path.EndsWith("/events", StringComparison.Ordinal)) streams++;
            if (request.Method == HttpMethod.Get && path == "/agent/v1/sessions/sess_123/runs")
                return Json(new { items = new[] { new { run_id = runId } }, next_cursor = (string?)null });
            if (request.Method == HttpMethod.Get && path == "/agent/v1/runs/" + runId)
            {
                var initial = ++reads == 1;
                var status = initial ? "waiting_user" : nextStatus;
                return Json(new
                {
                    run_id = runId,
                    session_id = initial ? "sess_123" : nextSession,
                    workspace_id = "ws_test",
                    status,
                    history_wait_kind = (string?)null,
                    preset = "standard",
                    policy_snapshot_version = 1,
                    policy_snapshot_sha256 = new string('d', 64),
                    version = 1L,
                    last_sequence = 4L,
                    created_at = "2026-09-30T00:00:00Z",
                    updated_at = "2026-09-30T00:00:03Z",
                    terminal_at = status is "completed" or "failed" ? "2026-09-30T00:00:03Z" : null,
                    terminal_error_code = (string?)null,
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });
        using var http = new HttpClient(handler);
        var output = new StringWriter();
        var errors = new StringWriter();
        using var input = new StringReader("Continue\n/status\n/exit\n");
        using var commands = new AgentCommands(host, http, output, errors, input);

        var exit = await commands.ChatAsync(new ChatCommand("C:\\workspace", "sess_123", "standard", false), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(2, reads);
        Assert.Equal(["/sessions/sess_123/activate"], posts);
        Assert.Equal(0, streams);
        Assert.Contains(expectedError, errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("sess_123", output.ToString(), StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
