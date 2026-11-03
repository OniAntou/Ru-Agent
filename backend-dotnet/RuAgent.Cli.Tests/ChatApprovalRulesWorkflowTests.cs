using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class ChatApprovalRulesWorkflowTests
{
    internal const string Workspace = "C:\\synthetic-workspace";
    internal static readonly string WorkspaceId = "ws_" + new string('a', 32);
    internal static readonly string GlobalId = "rule_" + new string('b', 32);
    internal static readonly string ProjectId = "rule_" + new string('c', 32);
    internal static readonly PublicHostSession Session = new(new Uri("http://127.0.0.1:1/"), "synthetic-rules-token", null!);

    internal sealed class Host : HttpMessageHandler
    {
        internal List<string> Requests = [];
        internal List<string> Deletes = [];
        internal List<object> Global = [Rule(GlobalId, "global")];
        internal List<object> Project = [Rule(ProjectId, "project")];
        internal object[] Metadata = [new { id = "synthetic", workspace_path = Workspace, workspace_id = WorkspaceId }];
        internal bool DeleteChangesState = true, ThrowOnDelete, FailProject, MalformedGlobal, FailMetadata;
        internal Func<Uri, object>? MetadataPage;
        internal static object Rule(string id, string scope, string decision = "allow", string path = "C:\\synthetic-workspace\\out\\**") =>
            new { rule_id = id, scope, decision, matcher = new { tool_id = "write", canonical_path_pattern = path, argv_pattern = new[] { "--safe", "a b" } }, creation_order = 1, version = 1 };
        internal static HttpResponseMessage Json(object data, HttpStatusCode code = HttpStatusCode.OK) => new(code)
        { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-rules-token", request.Headers.Authorization.Parameter);
            var path = request.RequestUri.AbsolutePath;
            if (path == "/sessions/page")
                return Task.FromResult(FailMetadata ? Json(new { error_code = "unavailable" }, HttpStatusCode.ServiceUnavailable)
                    : Json(MetadataPage?.Invoke(request.RequestUri) ?? new { status = "ok", sessions = Metadata, next_cursor = (string?)null }));
            if (path == "/agent/v1/policies")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                if (request.RequestUri.Query.Contains("scope=global"))
                    return Task.FromResult(MalformedGlobal ? Json(new { unexpected = true }) : Json(new { items = Global }));
                Assert.Equal("?scope=project&workspace_id=" + WorkspaceId, request.RequestUri.Query);
                return Task.FromResult(FailProject ? Json(new { error_code = "project_read_failed" }, HttpStatusCode.ServiceUnavailable) : Json(new { items = Project }));
            }
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(Json(new { }, HttpStatusCode.NotFound));
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.True(request.Headers.TryGetValues("X-Interactive-Confirmed", out var interactive));
            Assert.Equal("true", Assert.Single(interactive));
            var id = path.Split('/')[^1];
            Assert.True(id == GlobalId || id == ProjectId);
            Deletes.Add(id);
            if (ThrowOnDelete) throw new HttpRequestException("synthetic lost acknowledgement");
            if (DeleteChangesState)
            {
                Global.RemoveAll(item => JsonSerializer.SerializeToElement(item).GetProperty("rule_id").GetString() == id);
                Project.RemoveAll(item => JsonSerializer.SerializeToElement(item).GetProperty("rule_id").GetString() == id);
            }
            return Task.FromResult(Json(new { rule_id = id, deleted = true }));
        }
    }

    internal static async Task Run(Host host, Func<ChoiceList, CancellationToken, Task<string?>>? picker = null,
        StringWriter? output = null, bool json = false, CancellationToken ct = default,
        Func<string, IReadOnlyList<string>, CancellationToken, Task>? document = null)
    {
        using var http = new HttpClient(host, disposeHandler: false);
        using var api = new AgentApiClient(Session, http);
        await new ChatApprovalRulesWorkflow(api, new SessionApiClient(Session, http))
            .ExecuteAsync(Workspace, picker, document, output ?? new StringWriter(), json, ct);
    }

    internal static Func<ChoiceList, CancellationToken, Task<string?>> Picks(params string?[] values)
    {
        var queue = new Queue<string?>(values);
        return (_, _) => Task.FromResult(queue.Dequeue());
    }

    [Fact]
    public async Task Read_only_listing_binds_project_to_exact_host_metadata_without_creating_a_session()
    {
        using var host = new Host(); var output = new StringWriter();
        await Run(host, output: output, json: true);
        using var response = JsonDocument.Parse(output.ToString());
        Assert.Equal("remembered_rules", response.RootElement.GetProperty("type").GetString());
        Assert.Equal(2, response.RootElement.GetProperty("items").GetArrayLength());
        Assert.Null(response.RootElement.GetProperty("project_notice").GetString());
        Assert.All(host.Requests, request => Assert.StartsWith("GET ", request));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("keep")]
    [InlineData("yes")]
    public async Task Removal_requires_the_exact_explicit_confirmation(string? answer)
    {
        using var host = new Host(); var step = 0;
        await Run(host, (list, _) =>
        {
            step++;
            if (step == 1) return Task.FromResult<string?>("rule:0");
            if (step == 2) return Task.FromResult<string?>("remove");
            if (step == 3) { Assert.Equal("keep", list.Current.Value); return Task.FromResult(answer); }
            return Task.FromResult<string?>(null);
        });
        Assert.Empty(host.Deletes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Confirmed_removal_deletes_only_the_selected_scoped_allowance_and_refreshes(int index)
    {
        using var host = new Host(); var output = new StringWriter();
        await Run(host, Picks("rule:" + index, "remove", "confirm_remove", null), output);
        Assert.Equal(index == 0 ? GlobalId : ProjectId, Assert.Single(host.Deletes));
        Assert.Contains("Removed saved allowance", output.ToString());
        Assert.Equal(1, host.Global.Count + host.Project.Count);
    }

    [Theory]
    [InlineData("ask")]
    [InlineData("deny")]
    public async Task Ask_and_deny_rules_cannot_be_removed_even_by_a_stale_picker(string decision)
    {
        using var host = new Host(); host.Global = [Host.Rule(GlobalId, "global", decision)];
        var step = 0;
        var error = await Assert.ThrowsAsync<AgentApiException>(() => Run(host, (list, _) =>
        {
            if (++step == 1) return Task.FromResult<string?>("rule:0");
            Assert.False(list.Items.Single(item => item.Value == "remove").Enabled);
            return Task.FromResult<string?>("remove");
        }));
        Assert.Equal("rule_removal_refused", error.ErrorCode); Assert.Empty(host.Deletes);
    }

    [Fact]
    public async Task Changed_rule_is_not_deleted_after_confirmation()
    {
        using var host = new Host(); var step = 0;
        var error = await Assert.ThrowsAsync<AgentApiException>(() => Run(host, (_, _) =>
        {
            if (++step == 1) return Task.FromResult<string?>("rule:0");
            if (step == 2) return Task.FromResult<string?>("remove");
            host.Global = [Host.Rule(GlobalId, "global", "ask")];
            return Task.FromResult<string?>("confirm_remove");
        }));
        Assert.Equal("rule_changed", error.ErrorCode); Assert.Empty(host.Deletes);
    }

    [Fact]
    public async Task Workspace_identity_change_does_not_retarget_a_selected_project_rule()
    {
        using var host = new Host(); var step = 0;
        var error = await Assert.ThrowsAsync<AgentApiException>(() => Run(host, (_, _) =>
        {
            if (++step == 1) return Task.FromResult<string?>("rule:1");
            if (step == 2) return Task.FromResult<string?>("remove");
            host.Metadata = [new { workspace_path = "C:\\different-project", workspace_id = WorkspaceId }];
            return Task.FromResult<string?>("confirm_remove");
        }));
        Assert.Equal("rule_scope_changed", error.ErrorCode); Assert.Empty(host.Deletes);
    }

    [Fact]
    public async Task Cancellation_after_confirmation_never_sends_delete()
    {
        using var host = new Host(); using var stop = new CancellationTokenSource(); var step = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(host, (_, _) =>
        {
            if (++step == 1) return Task.FromResult<string?>("rule:0");
            if (step == 2) return Task.FromResult<string?>("remove");
            stop.Cancel(); return Task.FromResult<string?>("confirm_remove");
        }, ct: stop.Token));
        Assert.Empty(host.Deletes);
    }

    [Fact]
    public async Task Lost_delete_acknowledgement_is_not_retried_or_reported_as_success()
    {
        using var host = new Host { ThrowOnDelete = true }; var output = new StringWriter();
        await Assert.ThrowsAsync<HttpRequestException>(() => Run(host, Picks("rule:0", "remove", "confirm_remove"), output));
        Assert.Single(host.Deletes); Assert.DoesNotContain("Removed", output.ToString());
    }

    [Fact]
    public async Task A_success_http_status_without_actual_removal_is_not_reported_as_success()
    {
        using var host = new Host { DeleteChangesState = false }; var output = new StringWriter();
        var error = await Assert.ThrowsAsync<AgentApiException>(() => Run(host, Picks("rule:0", "remove", "confirm_remove"), output));
        Assert.Equal("rule_removal_unconfirmed", error.ErrorCode);
        Assert.Single(host.Deletes); Assert.DoesNotContain("Removed", output.ToString());
    }

    [Theory]
    [InlineData("rule_/../project", "global")]
    [InlineData("valid", "project")]
    public async Task Invalid_identity_or_scope_never_opens_a_removable_row(string id, string scope)
    {
        using var host = new Host(); host.Global = [Host.Rule(id == "valid" ? GlobalId : id, scope)];
        var error = await Assert.ThrowsAsync<AgentApiException>(() => Run(host));
        Assert.Equal("rules_response_invalid", error.ErrorCode); Assert.Empty(host.Deletes);
    }

    [Fact]
    public async Task Malformed_policy_envelope_is_an_error_rather_than_an_empty_list()
    {
        using var host = new Host { MalformedGlobal = true };
        var error = await Assert.ThrowsAsync<AgentApiException>(() => Run(host));
        Assert.Equal("invalid_response", error.ErrorCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Project_read_failure_or_missing_identity_is_explicit_and_keeps_global_rules(bool fail)
    {
        using var host = new Host { FailProject = fail };
        if (!fail) host.Metadata = [];
        var output = new StringWriter(); await Run(host, output: output, json: true);
        using var response = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, response.RootElement.GetProperty("items").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(response.RootElement.GetProperty("project_notice").GetString()));
        Assert.Empty(host.Deletes);
    }

    [Fact]
    public async Task Details_keep_full_matcher_and_sanitize_terminal_controls_without_deleting()
    {
        using var host = new Host(); host.Global = [Host.Rule(GlobalId, "global", path: "C:\\target\x1b[31m\\**")];
        var seen = false;
        await Run(host, Picks("rule:0", "details", null), document: (_, rows, _) =>
        {
            seen = true; var text = string.Join('\n', rows);
            Assert.Contains("argv_pattern", text); Assert.Contains(GlobalId, text); Assert.DoesNotContain('\x1b', text);
            return Task.CompletedTask;
        });
        Assert.True(seen); Assert.Empty(host.Deletes);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("foreign")]
    [InlineData("relative")]
    [InlineData("ambiguous")]
    [InlineData("truncated")]
    [InlineData("cursor_loop")]
    public async Task Uncertain_workspace_identity_never_reads_or_mutates_project_rules(string kind)
    {
        using var host = new Host { FailMetadata = kind == "failed" };
        if (kind == "foreign") host.Metadata = [new { workspace_path = "C:\\foreign", workspace_id = WorkspaceId }];
        if (kind == "relative") host.Metadata = [new { workspace_path = "synthetic-workspace", workspace_id = WorkspaceId }];
        if (kind == "ambiguous") host.Metadata = [
            new { workspace_path = Workspace, workspace_id = WorkspaceId },
            new { workspace_path = Workspace, workspace_id = "ws_" + new string('d', 32) }];
        if (kind == "truncated") host.MetadataPage = _ => new { status = "ok", sessions = host.Metadata, sessions_truncated = true };
        if (kind == "cursor_loop") host.MetadataPage = _ => new { status = "ok", sessions = host.Metadata, next_cursor = "repeated" };
        var output = new StringWriter(); await Run(host, output: output, json: true);
        using var response = JsonDocument.Parse(output.ToString());
        Assert.Single(response.RootElement.GetProperty("items").EnumerateArray());
        Assert.False(string.IsNullOrEmpty(response.RootElement.GetProperty("project_notice").GetString()));
        if (kind == "ambiguous") Assert.Contains("rules_workspace_ambiguous", response.RootElement.GetProperty("project_notice").GetString());
        if (kind is "truncated" or "cursor_loop") Assert.Contains("rules_workspace_unavailable", response.RootElement.GetProperty("project_notice").GetString());
        Assert.DoesNotContain(host.Requests, request => request.Contains("scope=project"));
        Assert.Empty(host.Deletes);
    }

    [Fact]
    public async Task Workspace_metadata_pagination_reaches_a_later_matching_workspace()
    {
        using var host = new Host();
        host.MetadataPage = uri => uri.Query.Contains("cursor=later")
            ? new { status = "ok", sessions = host.Metadata, next_cursor = (string?)null }
            : new { status = "ok", sessions = Array.Empty<object>(), next_cursor = (string?)"later" };
        var output = new StringWriter(); await Run(host, output: output, json: true);
        using var response = JsonDocument.Parse(output.ToString());
        Assert.Equal(2, response.RootElement.GetProperty("items").GetArrayLength());
        Assert.Null(response.RootElement.GetProperty("project_notice").GetString());
        Assert.Equal(2, host.Requests.Count(request => request.Contains("/sessions/page")));
    }

    [Fact]
    public async Task Duplicate_rule_ids_are_refused_before_selection()
    {
        using var host = new Host(); host.Global.Add(Host.Rule(GlobalId, "global"));
        var error = await Assert.ThrowsAsync<AgentApiException>(() => Run(host, (_, _) => throw new Exception("Must not open picker")));
        Assert.Equal("rules_response_invalid", error.ErrorCode); Assert.Empty(host.Deletes);
    }

    [Theory]
    [InlineData("rule:-1")]
    [InlineData("rule:2")]
    [InlineData("rule:99999999999999999")]
    public async Task Invalid_picker_values_cannot_target_a_rule(string value)
    {
        using var host = new Host();
        var error = await Assert.ThrowsAsync<AgentApiException>(() => Run(host, Picks(value)));
        Assert.Equal("rules_selection_invalid", error.ErrorCode); Assert.Empty(host.Deletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chat_command_lists_rules_without_starting_a_run_or_changing_preset(bool json)
    {
        using var host = new Host(); using var http = new HttpClient(host);
        using var output = new StringWriter(); using var errors = new StringWriter();
        using var reader = new StringReader("/preset rules\n/exit\n");
        using var commands = new AgentCommands(Session, http, output, errors, reader);
        Assert.Equal(0, await commands.ChatAsync(new ChatCommand(Path.GetTempPath(), null, "standard", json, Classic: true), default));
        Assert.Contains(GlobalId, output.ToString());
        Assert.DoesNotContain("rules_unavailable", errors.ToString());
        Assert.All(host.Requests, request => Assert.StartsWith("GET ", request));
        if (json)
            foreach (var line in output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                using (JsonDocument.Parse(line)) { }
    }
}
