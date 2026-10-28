using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatInputWorkflowTests
{
    internal static readonly string RunId = "run_" + new string('a', 32);
    internal static readonly string InputId = "inp_" + new string('b', 32);
    internal const string SessionId = "sess_question";
    internal static readonly PublicHostSession Session = new(new Uri("http://127.0.0.1:1/"), "synthetic-token", null!);
    internal static ChatInputQuestion Question => new(RunId, SessionId, InputId, "Which environment?", ["staging", "production"]);
    internal static string Summary(string status = "waiting_input", int version = 5, string session = SessionId) => JsonSerializer.Serialize(new
    {
        run_id = RunId, session_id = session, workspace_id = "ws_question", status, preset = "standard",
        policy_snapshot_version = 1, policy_snapshot_sha256 = new string('a', 64), version, last_sequence = 5,
        created_at = "2026-10-25T00:00:00Z", updated_at = "2026-10-25T00:00:00Z", terminal_at = (string?)null,
    });
    internal static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    internal sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }

    [Theory]
    [InlineData("staging")]
    [InlineData("Tùy chọn riêng 🐉\nGiữ nguyên nội dung /exit")]
    public async Task Answer_stays_on_exact_run_and_input_and_deduplicates_acknowledged_replay(string answer)
    {
        var posts = 0; var prompts = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get) return Json(Summary());
            posts++;
            Assert.Equal($"/agent/v1/runs/{RunId}/inputs/{InputId}", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(answer, body.RootElement.GetProperty("answer").GetString());
            return Json(JsonSerializer.Serialize(new { input_id = InputId, status = "answered", run_state = "running" }));
        }));
        using var api = new AgentApiClient(Session, http);
        var workflow = new ChatInputWorkflow(api);
        Task<string?> Ask(ChatInputQuestion q, CancellationToken ct) { prompts++; Assert.Equal(InputId, q.InputId); Assert.Equal(Question.Options, q.Options); return Task.FromResult<string?>(answer); }
        Assert.Equal(ChatInputOutcome.Answered, await workflow.AnswerAsync(Question, Ask, default));
        Assert.Equal(ChatInputOutcome.AlreadyAnswered, await workflow.AnswerAsync(Question, Ask, default));
        Assert.Equal(1, posts); Assert.Equal(1, prompts);
    }

    [Fact]
    public async Task Escape_defers_without_any_mutation_and_allows_a_later_prompt()
    {
        var gets = 0;
        using var http = new HttpClient(new Handler(request =>
        { Assert.Equal(HttpMethod.Get, request.Method); gets++; return Task.FromResult(Json(Summary())); }));
        using var api = new AgentApiClient(Session, http);
        var workflow = new ChatInputWorkflow(api);
        for (var i = 0; i < 2; i++)
            Assert.Equal(ChatInputOutcome.Deferred, await workflow.AnswerAsync(Question, (_, _) => Task.FromResult<string?>(null), default));
        Assert.Equal(2, gets);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("version")]
    [InlineData("session")]
    public async Task Changed_run_after_edit_refuses_to_post(string change)
    {
        var gets = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Json(++gets == 1 ? Summary() : Summary(change == "status" ? "running" : "waiting_input",
                change == "version" ? 6 : 5, change == "session" ? "sess_other" : SessionId)));
        }));
        using var api = new AgentApiClient(Session, http);
        await Assert.ThrowsAsync<AgentApiException>(() => new ChatInputWorkflow(api).AnswerAsync(Question, (_, _) => Task.FromResult<string?>("staging"), default));
        Assert.Equal(2, gets);
    }

    [Fact]
    public async Task Cancel_while_editing_never_posts_a_default_answer()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(request =>
        { Assert.Equal(HttpMethod.Get, request.Method); return Task.FromResult(Json(Summary())); }));
        using var api = new AgentApiClient(Session, http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ChatInputWorkflow(api).AnswerAsync(Question, (_, _) =>
        { cancellation.Cancel(); return Task.FromResult<string?>("staging"); }, cancellation.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\0answer")]
    public async Task Invalid_answer_never_posts(string answer)
    {
        using var http = new HttpClient(new Handler(request =>
        { Assert.Equal(HttpMethod.Get, request.Method); return Task.FromResult(Json(Summary())); }));
        using var api = new AgentApiClient(Session, http);
        await Assert.ThrowsAsync<AgentApiException>(() => new ChatInputWorkflow(api).AnswerAsync(Question, (_, _) => Task.FromResult<string?>(answer), default));
        Assert.False(ChatInputWorkflow.IsValidAnswer(new string('x', 8193)));
        Assert.True(ChatInputWorkflow.IsValidAnswer(new string('x', 8192)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_or_uncertain_post_is_not_automatically_retried(bool uncertain)
    {
        var posts = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Json(Summary()));
            posts++;
            if (uncertain) throw new HttpRequestException("synthetic connection closed");
            return Task.FromResult(Json("{\"error_code\":\"input_stale\",\"message\":\"already answered\"}", HttpStatusCode.Conflict));
        }));
        using var api = new AgentApiClient(Session, http);
        await Assert.ThrowsAnyAsync<Exception>(() => new ChatInputWorkflow(api).AnswerAsync(Question, (_, _) => Task.FromResult<string?>("staging"), default));
        Assert.Equal(1, posts);
    }

    [Theory]
    [InlineData("wrong_session")]
    [InlineData("wrong_sequence")]
    [InlineData("incomplete")]
    [InlineData("resolved")]
    [InlineData("missing_payload")]
    [InlineData("oversize")]
    [InlineData("valid")]
    public async Task Recovery_requires_exact_durable_identity_and_cursor(string scenario)
    {
        string Frame(long sequence, string type, string session, string summary) =>
            $"id: {sequence}\ndata: {JsonSerializer.Serialize(new HarnessSafeEventEnvelope(1, "evt_test", RunId, session,
                scenario == "wrong_sequence" ? 99 : sequence, type, "2026-10-25T00:00:00Z", "waiting_input",
                scenario == "oversize" ? new string('x', 65537) : summary, scenario == "missing_payload" && sequence == 5 ? null : "pay_test"))}\n\n";
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Contains("live=0", request.RequestUri!.Query);
            var data = Frame(scenario is "incomplete" or "resolved" or "missing_payload" ? 4 : 5, "input.requested",
                scenario == "wrong_session" ? "sess_other" : SessionId, "input_required");
            if (scenario == "resolved") data += Frame(5, "run.state_changed", SessionId, "input_resolved");
            if (scenario == "missing_payload") data += Frame(5, "input.requested", SessionId, "input_required");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") });
        }));
        using var sse = new AgentSseClient(Session, http);
        var result = await ChatRunWorkflow.ReadPendingInputAsync(sse, JsonSerializer.Deserialize<GetAgentRunResponse>(Summary())!, default);
        Assert.Equal(scenario == "valid", result.Pending is not null);
        Assert.Equal(scenario == "valid", result.Error is null);
    }

    [Fact]
    public async Task Reopened_sequential_session_follows_pending_input_without_stealing_stdin_or_mutating_the_run()
    {
        var replayed = false;
        var payloadId = "payload_" + new string('d', 32);
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/sessions/{SessionId}/activate")
                return Task.FromResult(Json(JsonSerializer.Serialize(new { status = "ok", session = new { id = SessionId, title = "Question", messages = Array.Empty<object>() } })));
            Assert.Equal(HttpMethod.Get, request.Method);
            if (path == "/settings") return Task.FromResult(Json("{}"));
            if (path == $"/agent/v1/sessions/{SessionId}/runs")
                return Task.FromResult(Json(JsonSerializer.Serialize(new { items = new[] { new { run_id = RunId } } })));
            if (path == $"/agent/v1/runs/{RunId}") return Task.FromResult(Json(Summary()));
            if (path == $"/agent/v1/runs/{RunId}/events")
            {
                Assert.Contains("live=0", request.RequestUri.Query);
                replayed = true;
                var frame = JsonSerializer.Serialize(new HarnessSafeEventEnvelope(1, "evt_question", RunId, SessionId, 5,
                    "input.requested", "2026-10-25T00:00:00Z", "waiting_input", "input_required", payloadId));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent($"id: 5\ndata: {frame}\n\n", Encoding.UTF8, "text/event-stream") });
            }
            if (path == $"/agent/v1/runs/{RunId}/payloads/{payloadId}")
                return Task.FromResult(Json(JsonSerializer.Serialize(new { run_id = RunId, payload_id = payloadId, kind = "userinputrequest", encoding = "json",
                    content = JsonSerializer.Serialize(new { input_id = InputId, question = "Which environment?", options = new[] { "staging" } }) })));
            return Task.FromResult(Json("{}", HttpStatusCode.NotFound));
        }));
        var output = new StringWriter(); var errors = new StringWriter();
        using var commands = new AgentCommands(Session, http, output, errors, new StringReader("/follow\n/exit\n"));
        Assert.Equal(0, await commands.ChatAsync(new ChatCommand(@"C:\workspace", SessionId, "standard", false), default).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(replayed);
        Assert.Contains("waiting for an answer", output.ToString());
        Assert.Contains($"ru agent answer {RunId} {InputId}", errors.ToString());
        Assert.DoesNotContain("run_not_followable", errors.ToString());
    }
}
