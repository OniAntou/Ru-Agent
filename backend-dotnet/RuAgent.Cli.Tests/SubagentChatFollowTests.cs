using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class SubagentChatFollowTests
{
    private const string SessionId = "sess_children";
    private static readonly string RunId = "run_" + new string('c', 32);
    private const string Stamp = "2026-10-20T00:00:00Z";

    [Theory]
    [InlineData("waiting_subagents", false, true)]
    [InlineData("waiting_subagents", true, false)]
    [InlineData("waiting_subagents_unknown", false, false)]
    [InlineData("waiting_user", false, false)]
    public void Only_active_subagent_waits_are_followable(string status, bool terminal, bool expected)
    {
        var run = JsonSerializer.Deserialize<GetAgentRunResponse>(Summary(status, terminal))!;
        Assert.Equal(expected, ChatRunWorkflow.IsFollowable(run));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reopened_session_can_follow_a_parent_waiting_for_children_from_its_cursor(bool explicitRun)
    {
        var followed = false;
        var mutation = false;
        long? cursor = null;
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/settings") return Json("{}");
            if (path == $"/sessions/{SessionId}/activate") return Json(JsonSerializer.Serialize(new { status = "ok", session = new { id = SessionId, title = "Children", messages = Array.Empty<object>() } }));
            if (path == $"/agent/v1/sessions/{SessionId}/runs") return Json(JsonSerializer.Serialize(new { items = new[] { new { run_id = RunId } } }));
            if (path == $"/agent/v1/runs/{RunId}") return Json(Summary(followed ? "completed" : "waiting_subagents", followed));
            if (path == $"/agent/v1/runs/{RunId}/events")
            {
                cursor = long.Parse(request.RequestUri.Query.Split("after_sequence=")[1].Split('&')[0]);
                followed = true;
                var delta = JsonSerializer.Serialize(new { kind = "assistant.delta", data = JsonSerializer.Serialize(new { text = "subagent result integrated", model_step_id = "step_" + new string('d', 32) }) });
                var complete = JsonSerializer.Serialize(new { protocol_version = 1, event_id = "evt_complete", run_id = RunId, session_id = SessionId,
                    sequence = 6, event_type = "run.state_changed", occurred_at = Stamp, status = "completed", safe_summary = "completed" });
                return new(HttpStatusCode.OK) { Content = new StringContent($"id: t1\nevent: transient\ndata: {delta}\n\nid: 6\ndata: {complete}\n\n", Encoding.UTF8, "text/event-stream") };
            }
            if (request.Method != HttpMethod.Get) mutation = true;
            return Json("{}", HttpStatusCode.NotFound);
        }));
        var output = new StringWriter(); var errors = new StringWriter();
        using var commands = new AgentCommands(new(new Uri("http://127.0.0.1:1/"), "synthetic-token", null!), http, output, errors,
            new StringReader((explicitRun ? $"/follow {RunId}" : "/follow") + "\n/exit\n"));
        var exit = await commands.ChatAsync(new ChatCommand(@"C:\workspace", SessionId, "standard", false), default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, exit);
        Assert.True(followed, "TUI must follow the existing parent while it waits for subagents.");
        Assert.Equal(5, cursor);
        Assert.Contains("subagent result integrated", output.ToString());
        Assert.Contains($"/follow {RunId}", output.ToString());
        Assert.False(mutation, "Following children must not create, continue or cancel a run.");
        Assert.DoesNotContain("run_not_followable", errors.ToString());
    }

    private static string Summary(string status, bool terminal) => JsonSerializer.Serialize(new
    {
        run_id = RunId, session_id = SessionId, workspace_id = "ws_children", status, history_wait_kind = (string?)null,
        preset = "standard", policy_snapshot_version = 1, policy_snapshot_sha256 = new string('a', 64), version = 5,
        last_sequence = terminal ? 6 : 5, created_at = Stamp, updated_at = Stamp, terminal_at = terminal ? Stamp : null, terminal_error_code = (string?)null,
    });
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
