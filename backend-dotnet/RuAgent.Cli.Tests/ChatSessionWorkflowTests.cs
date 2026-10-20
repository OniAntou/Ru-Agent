using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Cli;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatSessionWorkflowTests
{
    private const string CurrentSession = "sess_current";
    private const string NewSession = "sess_created";
    private static readonly string ActiveRun = "run_" + new string('b', 32);
    private static readonly string StaleApproval = "ap_" + new string('c', 32);
    private static readonly string PendingApproval = "ap_" + new string('d', 32);

    [Fact]
    public void SubmissionSnapshot_RejectsBackendOrModelChangedAfterAdmission()
    {
        var snapshot = ChatSubmissionSnapshot.Create(
            "accepted prompt", CurrentSession, "C:\\workspace", "standard", "OpenAI", "model-a", [], []);
        using var changed = JsonDocument.Parse("{\"backend\":\"OpenAI\",\"openai_model\":\"model-b\"}");
        using var unchanged = JsonDocument.Parse("{\"backend\":\"OpenAI\",\"openai_model\":\"model-a\"}");

        Assert.False(snapshot.MatchesSelection(changed.RootElement));
        Assert.True(snapshot.MatchesSelection(unchanged.RootElement));
    }

    private static PublicHostSession Session()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var descriptor = new RuAgent.Protocol.Harness.RuntimeDescriptor(
            1,
            "http://127.0.0.1:8000/",
            "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            proc.Id,
            proc.StartTime.ToUniversalTime().ToString("O"),
            new string('a', 32),
            DateTimeOffset.UtcNow.ToString("O"));
        return PublicHostSession.FromDescriptor(descriptor);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    [Fact]
    public async Task ChatAsync_SearchSessionsAndCreateNewSession_KeepSessionStateTransactional()
    {
        var requests = new List<(string Method, string Path)>();
        var output = new StringWriter();
        var errors = new StringWriter();
        var commands = new AgentCommands(
            Session(),
            new HttpClient(new FakeHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                requests.Add((request.Method.Method, path));
                if (path.EndsWith("/settings", StringComparison.Ordinal))
                {
                    return Json("{\"status\":\"ok\"}");
                }

                if (request.Method == HttpMethod.Get && path == "/sessions")
                {
                    return Json(
                        "{\"status\":\"ok\",\"sessions\":[" +
                        "{\"id\":\"sess_match\",\"title\":\"Compiler cleanup\",\"message_count\":4}," +
                        "{\"id\":\"sess_other\",\"title\":\"Recipe notes\",\"message_count\":2}]}");
                }

                if (request.Method == HttpMethod.Post && path == "/sessions/new")
                {
                    return Json("{\"status\":\"ok\",\"session_id\":\"" + NewSession + "\"}");
                }

                if (request.Method == HttpMethod.Post && path == $"/sessions/{NewSession}/activate")
                {
                    return Json("{\"status\":\"ok\",\"session\":{\"id\":\"" + NewSession + "\",\"messages\":[]}}");
                }

                return Json("{}");
            })),
            output,
            errors,
            new StringReader("/sessions compiler\n/new\n/status\n/exit\n"));

        var exit = await commands.ChatAsync(
            new ChatCommand("C:\\workspace", CurrentSession, "standard", Json: true),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("Compiler cleanup", errors.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Recipe notes", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains(NewSession, errors.ToString(), StringComparison.Ordinal);
        Assert.Contains(("POST", "/sessions/new"), requests);
    }

    [Fact]
    public async Task ChatAsync_ResumeMissingSession_LeavesCurrentSessionActive()
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var requests = new List<string>();
        var commands = new AgentCommands(
            Session(),
            new HttpClient(new FakeHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                requests.Add(path);
                if (path.EndsWith("/settings", StringComparison.Ordinal))
                {
                    return Json("{\"status\":\"ok\"}");
                }

                if (path == "/sessions/sess_missing/activate")
                {
                    return Json("{\"status\":\"error\",\"message\":\"session not found\"}", HttpStatusCode.NotFound);
                }

                return Json("{}");
            })),
            output,
            errors,
            new StringReader("/resume sess_missing\n/status\n/exit\n"));

        var exit = await commands.ChatAsync(
            new ChatCommand("C:\\workspace", CurrentSession, "standard", Json: true),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(CurrentSession, errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("session_get_failed", errors.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/sessions/sess_missing/rewind", requests, StringComparer.Ordinal);
    }

    [Fact]
    public async Task ChatAsync_SwitchSession_DoesNotCarryPriorSessionFollowableRun()
    {
        const string targetSession = "sess_target";
        var errors = new StringWriter();
        var requests = new List<string>();
        var commands = new AgentCommands(
            Session(),
            new HttpClient(new FakeHandler(request =>
            {
                var path = request.RequestUri!.PathAndQuery;
                requests.Add(path);
                if (path.EndsWith("/settings", StringComparison.Ordinal))
                {
                    return Json("{\"status\":\"ok\"}");
                }

                if (path == $"/agent/v1/sessions/{CurrentSession}/runs?limit=100")
                {
                    return Json("{\"items\":[{\"run_id\":\"" + ActiveRun + "\"}]}");
                }

                if (path == $"/agent/v1/sessions/{targetSession}/runs?limit=100")
                {
                    return Json("{\"items\":[]}");
                }

                if (path == $"/agent/v1/runs/{ActiveRun}")
                {
                    return Json(RunSummary("running", lastSequence: 0));
                }

                if (path == $"/sessions/{targetSession}/activate")
                {
                    return Json("{\"status\":\"ok\",\"session\":{\"id\":\"" + targetSession + "\",\"messages\":[]}}");
                }

                return Json("{}");
            })),
            new StringWriter(),
            errors,
            new StringReader($"/resume {targetSession}\n/follow\n/exit\n"));

        var exit = await commands.ChatAsync(
            new ChatCommand("C:\\workspace", CurrentSession, "standard", Json: true),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains($"/agent/v1/sessions/{targetSession}/runs?limit=100", requests);
        Assert.Equal(1, requests.Count(path => path == $"/agent/v1/runs/{ActiveRun}"));
        Assert.Contains("no active run to follow", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatAsync_FollowWaitingApproval_ExposesOnlyLatestPendingApproval()
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var requests = new List<string>();
        var commands = new AgentCommands(
            Session(),
            new HttpClient(new FakeHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                requests.Add(path);
                if (path.EndsWith("/settings", StringComparison.Ordinal))
                {
                    return Json("{\"status\":\"ok\"}");
                }

                if (path == $"/agent/v1/runs/{ActiveRun}")
                {
                    return Json(RunSummary("waiting_approval", lastSequence: 2050));
                }

                if (path == $"/agent/v1/runs/{ActiveRun}/events")
                {
                    var oldApproval = ApprovalEvent(2047, StaleApproval, "running", "old approval");
                    var resolved = "{\"protocol_version\":1,\"event_id\":\"evt_2048\",\"run_id\":\"" + ActiveRun +
                        "\",\"session_id\":\"" + CurrentSession + "\",\"sequence\":2048," +
                        "\"event_type\":\"run.state_changed\",\"occurred_at\":\"2026-10-04T00:00:01Z\"," +
                        "\"status\":\"running\",\"safe_summary\":\"approval_resolved\"}";
                    var pendingApproval = ApprovalEvent(2049, PendingApproval, "waiting_approval", "current approval");
                    var latestState = "{\"protocol_version\":1,\"event_id\":\"evt_2050\",\"run_id\":\"" + ActiveRun +
                        "\",\"session_id\":\"" + CurrentSession + "\",\"sequence\":2050," +
                        "\"event_type\":\"run.state_changed\",\"occurred_at\":\"2026-10-04T00:00:03Z\"," +
                        "\"status\":\"waiting_approval\",\"safe_summary\":\"approval remains pending\"}";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            $"id: 2047\nevent: event\ndata: {oldApproval}\n\nid: 2048\nevent: event\ndata: {resolved}\n\nid: 2049\nevent: event\ndata: {pendingApproval}\n\nid: 2050\nevent: event\ndata: {latestState}\n\n",
                            Encoding.UTF8,
                            "text/event-stream"),
                    };
                }

                return Json("{}");
            })),
            output,
            errors,
            new StringReader($"/follow {ActiveRun}\n/exit\n"));

        var exit = await commands.ChatAsync(
            new ChatCommand("C:\\workspace", CurrentSession, "standard", Json: true),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains($"/agent/v1/runs/{ActiveRun}", requests);
        Assert.Contains($"/agent/v1/runs/{ActiveRun}/events", requests);
        Assert.DoesNotContain($"/agent/v1/runs/{ActiveRun}/approvals/{StaleApproval}", requests);
        Assert.Contains(PendingApproval, errors.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(StaleApproval, errors.ToString(), StringComparison.Ordinal);
    }

    private static string RunSummary(string status, long lastSequence, bool terminal = false) =>
        "{\"run_id\":\"" + ActiveRun + "\",\"session_id\":\"" + CurrentSession +
        "\",\"workspace_id\":\"workspace_1\",\"status\":\"" + status + "\"," +
        "\"history_wait_kind\":null,\"preset\":\"standard\",\"policy_snapshot_version\":1," +
        "\"policy_snapshot_sha256\":\"" + new string('a', 64) + "\",\"version\":1," +
        "\"last_sequence\":" + lastSequence + ",\"created_at\":\"2026-10-04T00:00:00Z\"," +
        "\"updated_at\":\"2026-10-04T00:00:00Z\",\"terminal_at\":" +
        (terminal ? "\"2026-10-04T00:00:01Z\"" : "null") + ",\"terminal_error_code\":null}";

    private static string ApprovalEvent(long sequence, string approvalId, string status, string summary) =>
        "{\"protocol_version\":1,\"event_id\":\"evt_" + sequence + "\",\"run_id\":\"" + ActiveRun +
        "\",\"session_id\":\"" + CurrentSession + "\",\"sequence\":" + sequence + "," +
        "\"event_type\":\"approval.requested\",\"occurred_at\":\"2026-10-04T00:00:00Z\"," +
        "\"status\":\"" + status + "\",\"safe_summary\":\"" + summary + "\",\"approval_id\":\"" + approvalId + "\"}";

    [Fact]
    public void SessionChoices_StripTerminalControlsAndIgnoreMalformedMessageCounts()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            "{\"sessions\":[{\"id\":\"sess_safe\",\"title\":\"\\u001b]0;title\\u0007 clean \\u001b[31mname\\u001b[0m\",\"message_count\":\"many\"}]}");

        var choices = ChatSessionWorkflow.Filter(document.RootElement, null);

        var choice = Assert.Single(choices);
        Assert.Equal("clean name", choice.Title);
        Assert.Equal(0, choice.MessageCount);
        Assert.DoesNotContain('\u001b', choice.Display);
        Assert.DoesNotContain('\u0007', choice.Display);
    }
}
