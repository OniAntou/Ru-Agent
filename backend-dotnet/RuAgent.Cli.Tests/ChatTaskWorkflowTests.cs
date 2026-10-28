using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatTaskWorkflowTests
{
    internal const string SessionId = "sess_tasks";
    internal static readonly string RunId = "run_" + new string('e', 32);
    internal const string ExecutionId = "exec_background_1";
    internal static readonly PublicHostSession Session = new(new Uri("http://127.0.0.1:1/"), "synthetic-token", null!);

    internal sealed class Host : HttpMessageHandler
    {
        internal List<string> Commands { get; } = [];
        internal string Owner = SessionId;
        internal string State = "running";
        internal bool GatewayUnavailable;
        internal bool UncertainKill;
        internal int Lists;
        internal Func<int, string>? StateForList;
        internal static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/sessions/{SessionId}/activate") return Json(new { status = "ok", session = new { id = SessionId, title = "Tasks", messages = Array.Empty<object>() } });
            if (path == "/settings") return Json(new { });
            if (path == $"/agent/v1/sessions/{SessionId}/runs") return Json(new { items = new[] { new { run_id = RunId } } });
            if (path == $"/agent/v1/runs/{RunId}") return Json(new
            {
                run_id = RunId, session_id = Owner, workspace_id = "ws_tasks", status = "completed", preset = "standard",
                policy_snapshot_version = 1, policy_snapshot_sha256 = new string('a', 64), version = 5, last_sequence = 5,
                created_at = "2026-10-25T00:00:00Z", updated_at = "2026-10-25T00:00:00Z", terminal_at = "2026-10-25T00:00:00Z",
            });
            Assert.Equal($"/agent/v1/runs/{RunId}/commands", path);
            Assert.Equal(HttpMethod.Post, request.Method);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var command = body.RootElement.GetProperty("text").GetString()!;
            Commands.Add(command);
            if (GatewayUnavailable) return Json(new { error_code = "broker_gateway_unavailable", message = "Broker unavailable" }, HttpStatusCode.ServiceUnavailable);
            if (command == "/tasks") { Lists++; State = StateForList?.Invoke(Lists) ?? State; }
            else
            {
                Assert.Equal("/tasks kill " + ExecutionId, command);
                if (UncertainKill) throw new HttpRequestException("synthetic connection closed");
                State = "killed";
            }
            return Json(new { run_id = RunId, status = "succeeded", code = "tasks", run_version = 5,
                data = new { killed = command != "/tasks", tasks = new[] { new { execution_id = ExecutionId, state = State, background = true, exit_code = (int?)null, command = "npm run dev" } } } });
        }
    }

    [Fact]
    public async Task List_discovers_a_completed_run_but_never_stops_its_background_process()
    {
        using var host = new Host(); using var http = new HttpClient(host); using var api = new AgentApiClient(Session, http);
        var output = new StringWriter();
        await new ChatTaskWorkflow(api).ExecuteAsync(SessionId, null, null, null, output, false, default);
        Assert.Equal(["/tasks"], host.Commands);
        Assert.Contains("npm run dev", output.ToString()); Assert.Contains(ExecutionId, output.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("keep")]
    [InlineData("stop")]
    public async Task Stop_requires_explicit_confirmation_and_only_targets_the_selected_execution(string? decision)
    {
        using var host = new Host(); using var http = new HttpClient(host); using var api = new AgentApiClient(Session, http);
        var output = new StringWriter();
        await new ChatTaskWorkflow(api).ExecuteAsync(SessionId, RunId, "kill " + ExecutionId,
            (list, _) => { Assert.Equal("keep", list.Current.Value); return Task.FromResult(decision); }, output, false, default);
        Assert.Equal(decision == "stop" ? 1 : 0, host.Commands.Count(line => line.StartsWith("/tasks kill")));
        if (decision == "stop") Assert.Contains("Stop requested", output.ToString());
    }

    [Fact]
    public async Task Completed_while_confirmation_open_is_not_stopped_again()
    {
        using var host = new Host { StateForList = count => count == 1 ? "running" : "exited" };
        using var http = new HttpClient(host); using var api = new AgentApiClient(Session, http);
        var error = await Assert.ThrowsAsync<AgentApiException>(() => new ChatTaskWorkflow(api).ExecuteAsync(SessionId, RunId, "kill " + ExecutionId,
            (_, _) => Task.FromResult<string?>("stop"), new StringWriter(), false, default));
        Assert.Equal("background_task_not_running", error.ErrorCode);
        Assert.DoesNotContain(host.Commands, command => command.StartsWith("/tasks kill"));
    }

    [Theory]
    [InlineData("kill exec_missing")]
    [InlineData("kill")]
    [InlineData("kill a b")]
    [InlineData("kill a\0b")]
    public async Task Invalid_or_unknown_target_never_dispatches_a_kill(string argument)
    {
        using var host = new Host(); using var http = new HttpClient(host); using var api = new AgentApiClient(Session, http);
        await Assert.ThrowsAsync<AgentApiException>(() => new ChatTaskWorkflow(api).ExecuteAsync(SessionId, RunId, argument, null, new StringWriter(), false, default));
        Assert.DoesNotContain(host.Commands, command => command.StartsWith("/tasks kill"));
    }

    [Fact]
    public async Task Foreign_run_is_refused_before_listing_or_stopping()
    {
        using var host = new Host { Owner = "sess_other" }; using var http = new HttpClient(host); using var api = new AgentApiClient(Session, http);
        var error = await Assert.ThrowsAsync<AgentApiException>(() => new ChatTaskWorkflow(api).ExecuteAsync(SessionId, null, RunId, null, new StringWriter(), false, default));
        Assert.Equal("run_session_mismatch", error.ErrorCode); Assert.Empty(host.Commands);
    }

    [Fact]
    public async Task Session_change_while_confirming_refuses_kill()
    {
        using var host = new Host(); using var http = new HttpClient(host); using var api = new AgentApiClient(Session, http);
        await Assert.ThrowsAsync<AgentApiException>(() => new ChatTaskWorkflow(api).ExecuteAsync(SessionId, RunId, "kill " + ExecutionId,
            (_, _) => { host.Owner = "sess_other"; return Task.FromResult<string?>("stop"); }, new StringWriter(), false, default));
        Assert.DoesNotContain(host.Commands, command => command.StartsWith("/tasks kill"));
    }

    [Fact]
    public async Task Lost_stop_response_is_not_retried_automatically()
    {
        using var host = new Host { UncertainKill = true }; using var http = new HttpClient(host); using var api = new AgentApiClient(Session, http);
        await Assert.ThrowsAsync<HttpRequestException>(() => new ChatTaskWorkflow(api).ExecuteAsync(SessionId, RunId, "kill " + ExecutionId, null, new StringWriter(), false, default));
        Assert.Single(host.Commands, command => command.StartsWith("/tasks kill"));
    }

    [Fact]
    public async Task Chat_tasks_is_handled_locally_and_surfaces_gateway_failure()
    {
        using var host = new Host { GatewayUnavailable = true }; using var http = new HttpClient(host);
        var errors = new StringWriter();
        using var commands = new AgentCommands(Session, http, new StringWriter(), errors, new StringReader("/tasks\n/exit\n"));
        Assert.Equal(0, await commands.ChatAsync(new ChatCommand(@"C:\workspace", SessionId, "standard", false), default));
        Assert.Equal(["/tasks"], host.Commands);
        Assert.Contains("broker_gateway_unavailable", errors.ToString());
        Assert.DoesNotContain("unknown_command", errors.ToString());
    }

    [Fact]
    public void Projection_filters_foreground_and_sanitizes_labels_without_enabling_unknown_states()
    {
        var data = JsonSerializer.SerializeToElement(new { tasks = new[] {
            new { execution_id = "exec_foreground", state = "running", background = false, command = "hidden" },
            new { execution_id = ExecutionId, state = "run\u001bning", background = true, command = "dev\u001b[2J" },
        } });
        var row = Assert.Single(ChatTaskWorkflow.Project(data));
        Assert.Equal("unknown", row.State); Assert.False(row.CanStop); Assert.DoesNotContain('\u001b', row.Command);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Json_list_or_explicit_stop_outputs_only_structured_json(bool stop)
    {
        using var host = new Host(); using var http = new HttpClient(host); using var api = new AgentApiClient(Session, http);
        var output = new StringWriter();
        await new ChatTaskWorkflow(api).ExecuteAsync(SessionId, RunId, stop ? "kill " + ExecutionId : null, null, output, true, default);
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal(stop ? "task_stop" : "tasks", result.RootElement.GetProperty("type").GetString());
    }
}
