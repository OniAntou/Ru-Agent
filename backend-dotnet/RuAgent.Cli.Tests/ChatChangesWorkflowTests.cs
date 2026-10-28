using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatChangesWorkflowTests
{
    internal static AgentActivityModel Edit(string id, string path = "login.cs", int? added = 2, int? removed = 1,
        long sequence = 1, AgentActivityState state = AgentActivityState.Completed, string? detail = null) =>
        new(id, AgentActivityKind.Edit, state, path, 4, null, null, null, added, removed, null, "succeeded", null, sequence, null, detail);

    internal sealed class Host : HttpMessageHandler
    {
        internal const string Owner = "sess_changes";
        internal static readonly string Run = "run_" + new string('f', 32);
        internal static readonly PublicHostSession Session = new(new Uri("http://127.0.0.1:1/"), "synthetic-token", null!);
        internal string SessionId = Owner;
        internal bool Live;
        internal bool Unavailable;
        internal int Reads;
        internal Task ActivitiesReady = Task.CompletedTask;
        internal IReadOnlyList<AgentActivityModel> Rows = [Edit("act_edit_1", "login.cs", detail: AgentEditPreviewCodec.Encode("old", "new"))];
        private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/sessions/{Owner}/activate") return Task.FromResult(Json(new { status = "ok", session = new { id = Owner, title = "Changes", messages = Array.Empty<object>() } }));
            Assert.Equal(HttpMethod.Get, request.Method);
            if (path == "/settings") return Task.FromResult(Json(new { }));
            if (path == $"/agent/v1/sessions/{Owner}/runs") return Task.FromResult(Json(new { items = new[] { new { run_id = Run } } }));
            if (path == $"/agent/v1/runs/{Run}") return Task.FromResult(Json(new {
                run_id = Run, session_id = SessionId, workspace_id = "ws_changes", status = Live ? "running" : "completed", preset = "standard",
                policy_snapshot_version = 1, policy_snapshot_sha256 = new string('a', 64), version = 5, last_sequence = 5,
                created_at = "2026-10-25T00:00:00Z", updated_at = "2026-10-25T00:00:00Z", terminal_at = Live ? null : "2026-10-25T00:00:00Z",
            }));
            Assert.Equal($"/agent/v1/runs/{Run}/activities", path);
            Reads++;
            if (Unavailable) return Task.FromResult(Json(new { error_code = "activities_unavailable", message = "History unavailable" }, HttpStatusCode.ServiceUnavailable));
            var rows = Rows.Select(row => JsonSerializer.Deserialize<JsonElement>(AgentActivityWireCodec.Serialize(new AgentActivityWire(
                row.ActivityId, row.Kind, row.State, FileName: row.FileName, LineStart: row.LineStart, LineEnd: row.LineStart, Added: row.Added, Removed: row.Removed,
                Detail: row.Detail, DurableSequence: row.DurableSequence)))).ToArray();
            return ReadyAsync();
            async Task<HttpResponseMessage> ReadyAsync()
            {
                await ActivitiesReady.WaitAsync(ct);
                return Json(new { activities = rows });
            }
        }
    }

    [Fact]
    public void Projection_groups_filename_labels_case_insensitively_and_counts_each_latest_completed_edit_once()
    {
        var first = Edit("edit_a", "Login.cs", 3, 1);
        var second = Edit("edit_b", "LOGIN.cs", 5, 2, 2);
        var result = ChatChangesWorkflow.Project([first, second, first, Edit("read_1") with { Kind = AgentActivityKind.Explore },
            Edit("pending", state: AgentActivityState.Started), Edit("failed", state: AgentActivityState.Failed)]);
        var file = Assert.Single(result.Files);
        Assert.Equal(8, result.Added); Assert.Equal(3, result.Removed); Assert.Equal(2, file.Edits.Count);
    }

    [Fact]
    public void Newer_failure_or_kind_change_does_not_resurrect_an_older_success()
    {
        var success = Edit("edit_a");
        Assert.Empty(ChatChangesWorkflow.Project([success, success with { State = AgentActivityState.Failed, DurableSequence = 2 }, success]).Files);
        Assert.Empty(ChatChangesWorkflow.Project([success, success with { Kind = AgentActivityKind.Status, DurableSequence = 2 }]).Files);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    public void Unknown_counts_stay_unknown_instead_of_becoming_zero(int? added)
    {
        var result = ChatChangesWorkflow.Project([Edit("a", added: added), Edit("b", "other.cs", 4)]);
        Assert.Null(result.Added); Assert.Equal(2, result.Removed);
        Assert.Equal("+?", ChatChangesWorkflow.Count(result.Added, "+"));
    }

    [Fact]
    public void Preview_uses_recorded_unicode_text_and_explicitly_reports_missing_evidence()
    {
        var detail = "@@json " + JsonSerializer.Serialize(new[] { "cũ 🐉\r\nA\u001b[2J", "mới\nB" });
        var file = Assert.Single(ChatChangesWorkflow.Project([Edit("a", detail: detail), Edit("b", sequence: 2)]).Files);
        var text = string.Join('\n', ChatChangesWorkflow.Preview(file, Host.Run));
        Assert.Contains("cũ 🐉", text); Assert.Contains("mới", text);
        Assert.Contains("Before:", text); Assert.Contains("After:", text);
        Assert.Contains("No recorded preview available", text); Assert.DoesNotContain('\u001b', text);
        Assert.Contains("Not net Git diff", text);
    }

    [Fact]
    public void Preview_and_projection_are_bounded()
    {
        var edits = Enumerable.Range(0, 500).Select(i => Edit("e" + i, sequence: i + 1, detail: AgentEditPreviewCodec.Encode("a\nb\nc", "x\ny\nz"))).ToArray();
        var file = Assert.Single(ChatChangesWorkflow.Project(edits).Files);
        var preview = ChatChangesWorkflow.Preview(file, Host.Run);
        Assert.True(preview.Count <= 2001); Assert.Contains("omitted", preview.Last());
        Assert.Throws<AgentApiException>(() => ChatChangesWorkflow.Project(Enumerable.Repeat(edits[0], ChatChangesWorkflow.MaxActivities + 1).ToArray()));
    }

    [Fact]
    public async Task Reopened_run_json_is_read_only_and_labels_its_scope()
    {
        using var host = new Host(); using var http = new HttpClient(host); using var api = new AgentApiClient(Host.Session, http);
        var output = new StringWriter();
        await new ChatChangesWorkflow(api).ExecuteAsync(Host.Owner, null, null, null, null, output, true, default);
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal("changes", result.RootElement.GetProperty("type").GetString());
        Assert.Equal(Host.Run, result.RootElement.GetProperty("run_id").GetString());
        Assert.True(result.RootElement.GetProperty("history_may_be_limited").GetBoolean());
        Assert.False(result.RootElement.GetProperty("full_paths_available").GetBoolean());
        Assert.False(result.RootElement.GetProperty("in_progress").GetBoolean());
        Assert.Equal(1, host.Reads);
    }

    [Fact]
    public async Task Picker_opens_recorded_preview_and_refreshes_only_on_explicit_actions()
    {
        using var host = new Host { Live = true }; using var http = new HttpClient(host); using var api = new AgentApiClient(Host.Session, http);
        var selections = new Queue<string?>(["0", null]);
        var previews = 0;
        await new ChatChangesWorkflow(api).ExecuteAsync(Host.Owner, Host.Run, null,
            (list, _) => { Assert.Contains(list.Items, item => item.Detail.Contains("+2")); return Task.FromResult(selections.Dequeue()); },
            (_, rows, _) => { previews++; Assert.Contains("  old", rows); Assert.Contains("  new", rows); return Task.CompletedTask; },
            new StringWriter(), false, default);
        Assert.Equal(1, previews); Assert.Equal(2, host.Reads);
    }

    [Fact]
    public async Task Foreign_session_refused_before_reading_history()
    {
        using var host = new Host { SessionId = "sess_other" }; using var http = new HttpClient(host); using var api = new AgentApiClient(Host.Session, http);
        var error = await Assert.ThrowsAsync<AgentApiException>(() => new ChatChangesWorkflow(api).ExecuteAsync(Host.Owner, null, Host.Run, null, null, new StringWriter(), false, default));
        Assert.Equal("run_session_mismatch", error.ErrorCode); Assert.Equal(0, host.Reads);
    }

    [Fact]
    public async Task Missing_history_is_an_error_not_an_empty_success()
    {
        using var host = new Host { Unavailable = true }; using var http = new HttpClient(host); using var api = new AgentApiClient(Host.Session, http);
        var output = new StringWriter();
        await Assert.ThrowsAsync<AgentApiException>(() => new ChatChangesWorkflow(api).ExecuteAsync(Host.Owner, Host.Run, null, null, null, output, false, default));
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public async Task Slash_command_is_integrated_without_creating_a_new_prompt_or_run()
    {
        using var host = new Host(); using var http = new HttpClient(host);
        var output = new StringWriter(); var errors = new StringWriter();
        using var commands = new AgentCommands(Host.Session, http, output, errors, new StringReader("/changes\n/exit\n"));
        Assert.Equal(0, await commands.ChatAsync(new ChatCommand(@"C:\workspace", Host.Owner, "standard", false), default));
        Assert.Contains("login.cs", output.ToString()); Assert.DoesNotContain("unknown_command", errors.ToString());
        Assert.Equal(1, host.Reads);
    }
}
