using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatRewindWorkflowTests
{
    internal sealed class Host : HttpMessageHandler
    {
        internal const string Owner = "sess_rewind";
        internal static readonly string Run = "run_" + new string('d', 32);
        internal static readonly PublicHostSession Session = new(new Uri("http://127.0.0.1:1/"), "synthetic-token", null!);
        internal JsonElement[] Messages = JsonSerializer.SerializeToElement(new[] {
            new { role = "user", content = "first task" }, new { role = "system", content = "hidden row" },
            new { role = "assistant", content = "first result" }, new { role = "user", content = "second task" },
            new { role = "assistant", content = "second result" },
        }).EnumerateArray().ToArray();
        internal string RunState = "completed";
        internal string SessionId = Owner;
        internal int Activations, Posts;
        internal int? PostedIndex;
        internal bool LoseResponse, WrongAcknowledgement, OmitRunFromList;
        internal Action<int>? OnActivate;
        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/settings") return Json(new { });
            if (path == $"/sessions/{Owner}/activate")
            { Activations++; OnActivate?.Invoke(Activations); return Json(new { status = "ok", session = new { id = SessionId, title = "Rewind", messages = Messages } }); }
            if (path == $"/agent/v1/sessions/{Owner}/runs") return Json(new { items = OmitRunFromList ? [] : new[] { new { run_id = Run } } });
            if (path == $"/agent/v1/runs/{Run}") return Json(new {
                run_id = Run, session_id = Owner, workspace_id = "ws_rewind", status = RunState, preset = "standard",
                policy_snapshot_version = 1, policy_snapshot_sha256 = new string('a', 64), version = 5, last_sequence = 5,
                created_at = "2026-10-25T00:00:00Z", updated_at = "2026-10-25T00:00:00Z", terminal_at = RunState == "completed" ? "2026-10-25T00:00:00Z" : null,
            });
            Assert.Equal($"/sessions/{Owner}/rewind", path); Assert.Equal(HttpMethod.Post, request.Method);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            PostedIndex = body.RootElement.GetProperty("to_message_index").GetInt32(); Posts++;
            Messages = Messages.Take(PostedIndex.Value + 1).ToArray();
            if (LoseResponse) { throw new HttpRequestException("synthetic response lost after commit"); }
            return Json(new { status = "ok", session = new { id = WrongAcknowledgement ? "sess_wrong" : Owner, title = "Rewind", messages = Messages } });
        }
    }

    [Fact]
    public void Targets_keep_raw_server_indexes_even_when_display_rows_are_hidden()
    {
        using var host = new Host();
        var targets = ChatRewindWorkflow.Targets(JsonSerializer.SerializeToElement(host.Messages));
        Assert.Equal(new[] { 0, 2, 3, 4 }, targets.Select(target => target.Index));
    }

    [Fact]
    public async Task Confirmed_picker_posts_exact_raw_index_and_keeps_selected_message_inclusively()
    {
        using var host = new Host(); using var http = new HttpClient(host); using var api = new AgentApiClient(Host.Session, http);
        var answers = new Queue<string?>(["3", "rewind"]);
        var workflow = new ChatRewindWorkflow(new SessionApiClient(Host.Session, http), api);
        var result = await workflow.ExecuteAsync(Host.Owner, null, () => true, (list, _) =>
        {
            if (answers.Count == 1) { Assert.Equal("keep", list.Current.Value); Assert.Contains("Files", list.Current.Detail); }
            return Task.FromResult(answers.Dequeue());
        }, new StringWriter(), false, default);
        Assert.True(result!.Applied); Assert.Equal(3, result.Kept); Assert.Equal(2, result.Removed);
        Assert.Equal(2, host.PostedIndex); Assert.Equal(1, host.Posts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("keep")]
    public async Task Cancel_confirmation_never_posts(string? decision)
    {
        using var host = new Host(); using var http = new HttpClient(host); using var api = new AgentApiClient(Host.Session, http);
        var result = await new ChatRewindWorkflow(new SessionApiClient(Host.Session, http), api).ExecuteAsync(Host.Owner, "1", () => true,
            (_, _) => Task.FromResult(decision), new StringWriter(), false, default);
        Assert.Null(result); Assert.Equal(0, host.Posts); Assert.Equal(5, host.Messages.Length);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("waiting_user")]
    [InlineData("waiting_input")]
    [InlineData("waiting_approval")]
    [InlineData("waiting_subagents")]
    [InlineData("reconciliation_required")]
    public async Task Any_known_nonterminal_run_blocks_rewind(string state)
    {
        using var host = new Host { RunState = state }; using var http = new HttpClient(host); using var api = new AgentApiClient(Host.Session, http);
        var error = await Assert.ThrowsAsync<AgentApiException>(() => new ChatRewindWorkflow(new SessionApiClient(Host.Session, http), api)
            .ExecuteAsync(Host.Owner, "1 --yes", () => true, null, new StringWriter(), false, default));
        Assert.Equal("session_busy", error.ErrorCode); Assert.Equal(0, host.Posts); Assert.Equal(0, host.Activations);
    }

    [Fact]
    public async Task Locally_retained_run_is_checked_even_if_missing_from_the_discovery_page()
    {
        using var host = new Host { RunState = "waiting_input", OmitRunFromList = true };
        using var http = new HttpClient(host); using var api = new AgentApiClient(Host.Session, http);
        var error = await Assert.ThrowsAsync<AgentApiException>(() => new ChatRewindWorkflow(new SessionApiClient(Host.Session, http), api)
            .ExecuteAsync(Host.Owner, "1 --yes", () => true, null, new StringWriter(), false, default, [Host.Run]));
        Assert.Equal("session_busy", error.ErrorCode); Assert.Equal(0, host.Posts); Assert.Equal(0, host.Activations);
    }

    [Fact]
    public async Task Changed_history_after_confirmation_refuses_a_stale_index()
    {
        using var host = new Host(); using var http = new HttpClient(host); using var api = new AgentApiClient(Host.Session, http);
        host.OnActivate = count => { if (count == 2) host.Messages = host.Messages.Take(4).ToArray(); };
        var error = await Assert.ThrowsAsync<AgentApiException>(() => new ChatRewindWorkflow(new SessionApiClient(Host.Session, http), api)
            .ExecuteAsync(Host.Owner, "1 --yes", () => true, null, new StringWriter(), false, default));
        Assert.Equal("rewind_stale", error.ErrorCode); Assert.Equal(0, host.Posts);
    }

    [Theory]
    [InlineData("0 --yes")]
    [InlineData("-1 --yes")]
    [InlineData("2 --yes")]
    [InlineData("99 --yes")]
    [InlineData("1 wrong")]
    public async Task Invalid_or_hidden_target_never_posts(string argument)
    {
        using var host = new Host(); using var http = new HttpClient(host); using var api = new AgentApiClient(Host.Session, http);
        await Assert.ThrowsAsync<AgentApiException>(() => new ChatRewindWorkflow(new SessionApiClient(Host.Session, http), api)
            .ExecuteAsync(Host.Owner, argument, () => true, null, new StringWriter(), false, default));
        Assert.Equal(0, host.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_or_wrong_acknowledgement_is_unknown_and_never_automatically_retried(bool wrongAck)
    {
        using var host = new Host { LoseResponse = !wrongAck, WrongAcknowledgement = wrongAck };
        using var http = new HttpClient(host); using var api = new AgentApiClient(Host.Session, http);
        var error = await Assert.ThrowsAsync<AgentApiException>(() => new ChatRewindWorkflow(new SessionApiClient(Host.Session, http), api)
            .ExecuteAsync(Host.Owner, "1 --yes", () => true, null, new StringWriter(), false, default));
        Assert.Equal("rewind_outcome_unknown", error.ErrorCode); Assert.Equal(1, host.Posts);
    }

    [Fact]
    public async Task Json_bare_rewind_lists_saved_targets_without_mutation()
    {
        using var host = new Host(); using var http = new HttpClient(host); using var api = new AgentApiClient(Host.Session, http);
        var output = new StringWriter();
        Assert.Null(await new ChatRewindWorkflow(new SessionApiClient(Host.Session, http), api).ExecuteAsync(Host.Owner, null, () => true, null, output, true, default));
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal("rewind_targets", result.RootElement.GetProperty("type").GetString()); Assert.Equal(0, host.Posts);
    }

    [Fact]
    public async Task Listing_beyond_replay_window_keeps_full_numbers_and_caps_only_the_picker()
    {
        using var host = new Host(); using var http = new HttpClient(host); using var api = new AgentApiClient(Host.Session, http);
        host.Messages = Enumerable.Range(0, 150).Select(index => JsonSerializer.SerializeToElement(new { role = "user", content = $"message {index}" })).ToArray();
        var output = new StringWriter();
        await new ChatRewindWorkflow(new SessionApiClient(Host.Session, http), api).ExecuteAsync(Host.Owner, null, () => true, null, output, true, default);
        using var document = JsonDocument.Parse(output.ToString());
        var targets = document.RootElement.GetProperty("targets");
        Assert.Equal(100, targets.GetArrayLength());
        Assert.Equal(150, targets[0].GetProperty("message_number").GetInt32());
        Assert.Equal(51, targets[99].GetProperty("message_number").GetInt32());
        Assert.Equal(0, host.Posts);
    }

    [Fact]
    public async Task Nonmodal_numeric_target_requires_explicit_confirmation()
    {
        using var host = new Host(); using var http = new HttpClient(host); using var api = new AgentApiClient(Host.Session, http);
        var error = await Assert.ThrowsAsync<AgentApiException>(() => new ChatRewindWorkflow(new SessionApiClient(Host.Session, http), api)
            .ExecuteAsync(Host.Owner, "1", () => true, null, new StringWriter(), false, default));
        Assert.Equal("interactive_required", error.ErrorCode); Assert.Equal(0, host.Posts);
    }

    [Fact]
    public async Task Cancelled_while_confirming_does_not_post()
    {
        using var host = new Host(); using var http = new HttpClient(host); using var api = new AgentApiClient(Host.Session, http);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ChatRewindWorkflow(new SessionApiClient(Host.Session, http), api)
            .ExecuteAsync(Host.Owner, "1", () => true, (_, _) => { cancellation.Cancel(); return Task.FromResult<string?>("rewind"); }, new StringWriter(), false, cancellation.Token));
        Assert.Equal(0, host.Posts);
    }

    [Fact]
    public async Task Legacy_tui_rewind_syntax_never_truncates_original_history()
    {
        using var host = new Host(); using var http = new HttpClient(host);
        var errors = new StringWriter();
        using var commands = new AgentCommands(Host.Session, http, new StringWriter(), errors,
            new StringReader("/rewind 1 --yes\n/exit\n"));
        Assert.Equal(0, await commands.ChatAsync(new ChatCommand(@"C:\workspace", Host.Owner, "standard", false), default));
        Assert.Contains("invalid_history_arguments", errors.ToString());
        Assert.Equal(0, host.Posts); Assert.Equal(5, host.Messages.Length);
    }
}
