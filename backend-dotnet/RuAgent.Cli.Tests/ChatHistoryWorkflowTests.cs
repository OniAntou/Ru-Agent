using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatHistoryWorkflowTests
{
    internal sealed class Host : HttpMessageHandler
    {
        internal static readonly PublicHostSession Session = new(new Uri("http://127.0.0.1:1/"), "synthetic-token", null!);
        internal JsonElement Messages = JsonSerializer.SerializeToElement(new[] {
            new { role = "user", content = "first\nrequest" }, new { role = "ai", content = "first answer" },
            new { role = "user", content = "second request" }, new { role = "ai", content = "second answer" } });
        internal int Forks, Keep;
        internal int MessageOffset;
        internal bool LoseResponse, WrongPrefix, Stale;
        internal List<string> PolicySessions = [];
        internal List<string> Requests = [];
        internal static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath; Requests.Add(request.Method + " " + path);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (path == "/settings") return Json(new { status = "ok", backend = "OpenAI", openai_model = "model-a" });
            if (path.EndsWith("/activate")) return Json(new { status = "ok", session = new {
                id = path.Contains("/child/") ? "child" : "source", title = "History", messages = path.Contains("/child/") ? Messages.EnumerateArray().Take(Keep) : Messages.EnumerateArray(),
                message_offset = MessageOffset, total_message_count = MessageOffset + Messages.GetArrayLength(), history_revision = new string('a', 64) } });
            if (path == "/sessions/source/fork")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Keep = body.RootElement.GetProperty("keep_messages").GetInt32(); Forks++;
                Assert.Equal(new string('a', 64), body.RootElement.GetProperty("history_revision").GetString());
                if (Stale) return Json(new { status = "error", error_code = "history_stale" }, HttpStatusCode.BadRequest);
                if (LoseResponse) throw new HttpRequestException("synthetic response lost after commit");
                var history = Enumerable.Range(0, MessageOffset).Select(i => JsonSerializer.SerializeToElement(
                    new { role = i % 2 == 0 ? "user" : "ai", content = "older " + i })).Concat(Messages.EnumerateArray());
                var childOffset = Math.Max(0, Keep - 200);
                return Json(new { status = "ok", parent_session_id = "source", session = new { id = "child", title = "Branch",
                    message_offset = childOffset, total_message_count = Keep,
                    messages = history.Take(WrongPrefix ? MessageOffset + 4 : Keep).Skip(childOffset) } });
            }
            if (path.Contains("/policies") && request.Content is not null)
            {
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
                PolicySessions.Add(body.RootElement.GetProperty("session_id").GetString()!);
                return Json(new { }, HttpStatusCode.ServiceUnavailable);
            }
            if (path.EndsWith("/preset")) return Json(new { }, HttpStatusCode.ServiceUnavailable);
            if (path == "/sessions") return Json(new { status = "ok", sessions = new[] { new { id = "source", title = "Original", message_count = 4 } } });
            return Json(new { status = "ok", items = Array.Empty<object>(), models = Array.Empty<object>(), provider_ids = Array.Empty<object>(), providers = Array.Empty<object>() });
        }
    }

    [Theory]
    [InlineData("1 edit", 20)]
    [InlineData("1 continue", 22)]
    [InlineData("2 continue", 24)]
    [InlineData("2 continue", 224)]
    public async Task Paged_history_uses_absolute_boundaries_and_validates_the_returned_window(string command, int keep)
    {
        var offset = keep > 200 ? 220 : 20;
        using var host = new Host { MessageOffset = offset }; using var http = new HttpClient(host);
        using var agents = new AgentApiClient(Host.Session, http);
        var workflow = new ChatHistoryWorkflow(new SessionApiClient(Host.Session, http), agents);
        var plan = await workflow.SelectAsync("source", command, () => true, null, null, new StringWriter(), false, default);
        Assert.NotNull(plan); Assert.Equal(keep, plan.KeepMessages); Assert.Equal(offset, plan.MessageOffset);
        await workflow.CommitAsync(plan, default);
        Assert.Equal(keep, host.Keep); Assert.Equal(1, host.Forks);
    }

    [Theory]
    [InlineData("1 edit", 0, "first\nrequest")]
    [InlineData("1 continue", 2, null)]
    [InlineData("2 edit", 2, "second request")]
    [InlineData("2 continue", 4, null)]
    public async Task Select_is_read_only_and_commit_copies_exact_turn_boundary(string command, int keep, string? draft)
    {
        using var host = new Host(); using var http = new HttpClient(host); using var agents = new AgentApiClient(Host.Session, http);
        var workflow = new ChatHistoryWorkflow(new SessionApiClient(Host.Session, http), agents);
        var plan = await workflow.SelectAsync("source", command, () => true, null, null, new StringWriter(), false, default);
        Assert.NotNull(plan); Assert.Equal(keep, plan.KeepMessages); Assert.Equal(draft, plan.Draft); Assert.Equal(0, host.Forks);
        var result = await workflow.CommitAsync(plan, default);
        Assert.Equal("child", result.GetProperty("session").GetProperty("id").GetString());
        Assert.Equal(1, host.Forks); Assert.Equal(4, host.Messages.GetArrayLength());
        Assert.DoesNotContain(host.Requests, r => r.Contains("/rewind") || r.Contains("/agent/v1/runs"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Browsing_or_cancelling_never_creates_session(bool view)
    {
        using var host = new Host(); using var http = new HttpClient(host); using var agents = new AgentApiClient(Host.Session, http);
        var answers = new Queue<string?>(view ? ["1", "view", null] : [null]); var documents = 0;
        var result = await new ChatHistoryWorkflow(new SessionApiClient(Host.Session, http), agents).SelectAsync("source", null, () => true,
            (_, _) => Task.FromResult(answers.Dequeue()), (_, rows, _) => { documents++; Assert.Contains("first answer", rows); return Task.CompletedTask; }, new StringWriter(), false, default);
        Assert.Null(result); Assert.Equal(view ? 1 : 0, documents); Assert.Equal(0, host.Forks);
    }

    [Theory]
    [InlineData("0 edit")]
    [InlineData("99 continue")]
    [InlineData("1 --yes")]
    public async Task Invalid_target_or_old_destructive_syntax_is_refused(string argument)
    {
        using var host = new Host(); using var http = new HttpClient(host); using var agents = new AgentApiClient(Host.Session, http);
        await Assert.ThrowsAsync<AgentApiException>(() => new ChatHistoryWorkflow(new SessionApiClient(Host.Session, http), agents)
            .SelectAsync("source", argument, () => true, null, null, new StringWriter(), false, default));
        Assert.Equal(0, host.Forks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_invalid_acknowledgement_is_unknown_and_not_retried(bool wrongPrefix)
    {
        using var host = new Host { LoseResponse = !wrongPrefix, WrongPrefix = wrongPrefix }; using var http = new HttpClient(host);
        using var agents = new AgentApiClient(Host.Session, http);
        var workflow = new ChatHistoryWorkflow(new SessionApiClient(Host.Session, http), agents);
        var plan = await workflow.SelectAsync("source", "1 edit", () => true, null, null, new StringWriter(), false, default);
        var error = await Assert.ThrowsAsync<AgentApiException>(() => workflow.CommitAsync(plan!, default));
        Assert.Equal("history_branch_unknown", error.ErrorCode); Assert.Equal(1, host.Forks);
    }

    [Theory]
    [InlineData("/history 1 edit\nrevised request\n/exit\n", 1)]
    [InlineData("/history 1 continue\nnext approach\n/exit\n", 1)]
    [InlineData("/history 1 edit\n/history cancel\n/exit\n", 0)]
    [InlineData("/history 1 view\n/exit\n", 0)]
    public async Task Chat_creates_branch_only_on_send_and_uses_child_policy_scope(string input, int forks)
    {
        using var host = new Host(); using var http = new HttpClient(host); var errors = new StringWriter();
        using var commands = new AgentCommands(Host.Session, http, new StringWriter(), errors, new StringReader(input));
        Assert.Equal(0, await commands.ChatAsync(new ChatCommand(Path.GetTempPath(), "source", "standard", false), default));
        Assert.Equal(forks, host.Forks);
        if (forks > 0) { Assert.NotEmpty(host.PolicySessions); Assert.All(host.PolicySessions, id => Assert.Equal("child", id)); }
        Assert.DoesNotContain(host.Requests, r => r.Contains("/rewind"));
        Assert.DoesNotContain("history_error", errors.ToString());
    }

    [Fact]
    public async Task Stale_branch_is_a_definite_rejection_and_never_auto_retried()
    {
        using var host = new Host { Stale = true }; using var http = new HttpClient(host); using var agents = new AgentApiClient(Host.Session, http);
        var workflow = new ChatHistoryWorkflow(new SessionApiClient(Host.Session, http), agents);
        var plan = await workflow.SelectAsync("source", "1 edit", () => true, null, null, new StringWriter(), false, default);
        var error = await Assert.ThrowsAsync<AgentApiException>(() => workflow.CommitAsync(plan!, default));
        Assert.Equal("history_stale", error.ErrorCode); Assert.Equal(1, host.Forks);
    }

    [Fact]
    public async Task Busy_session_refuses_before_reading_or_creating()
    {
        using var host = new Host(); using var http = new HttpClient(host); using var agents = new AgentApiClient(Host.Session, http);
        var error = await Assert.ThrowsAsync<AgentApiException>(() => new ChatHistoryWorkflow(new SessionApiClient(Host.Session, http), agents)
            .SelectAsync("source", null, () => false, null, null, new StringWriter(), false, default));
        Assert.Equal("session_busy", error.ErrorCode); Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task Json_listing_numbers_turns_without_creating_any_branch()
    {
        using var host = new Host(); using var http = new HttpClient(host); using var agents = new AgentApiClient(Host.Session, http);
        var output = new StringWriter();
        await new ChatHistoryWorkflow(new SessionApiClient(Host.Session, http), agents).SelectAsync("source", null, () => true, null, null, output, true, default);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(new[] { 2, 1 }, json.RootElement.GetProperty("turns").EnumerateArray().Select(t => t.GetProperty("turn").GetInt32()));
        Assert.Equal(0, host.Forks);
    }

    [Fact]
    public async Task Unknown_branch_blocks_next_send_until_session_reload()
    {
        using var host = new Host { LoseResponse = true }; using var http = new HttpClient(host); var errors = new StringWriter();
        using var commands = new AgentCommands(Host.Session, http, new StringWriter(), errors,
            new StringReader("/history 1 edit\nfirst send\nsecond send\n/exit\n"));
        await commands.ChatAsync(new ChatCommand(Path.GetTempPath(), "source", "standard", false), default);
        Assert.Equal(1, host.Forks); Assert.Empty(host.PolicySessions);
        Assert.Contains("history_reload_required", errors.ToString());
    }
}
