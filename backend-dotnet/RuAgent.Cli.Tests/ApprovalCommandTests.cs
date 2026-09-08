using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ApprovalCommandTests
{
    private static PublicHostSession CreateSession(string bearer = "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuAgent.Protocol.Harness.RuntimeDescriptor(1, "http://127.0.0.1:8000/", bearer, proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        return PublicHostSession.FromDescriptor(desc);
    }

    private static HttpClient CreateFakeClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var fake = new FakeHandler(handler);
        return new HttpClient(fake);
    }

    [Fact]
    public async Task Approve_SendsAllowOnceCommandAndShowsPreview()
    {
        var session = CreateSession();
        var runId = "run_" + new string('a', 32);
        var approvalId = "apr_" + new string('b', 32);
        bool commandChecked = false;
        var handler = CreateFakeClient(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.Contains($"/approvals/{approvalId}"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                var doc = JsonDocument.Parse(body);
                Assert.Equal("allow_once", doc.RootElement.GetProperty("command").GetString());
                commandChecked = true;
                var respJson = JsonSerializer.Serialize(new
                {
                    approval_id = approvalId,
                    status = "authorized",
                    matcher_preview = new { tool_id = "files.read", canonical_path = "probe.txt", scope = "once" },
                    rule_saved = false
                });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respJson, Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var commands = new AgentCommands(session, handler, outWriter, errWriter);
        var cmd = new ApproveCommand(runId, approvalId, ApprovalScope.Once, false);
        var exit = await commands.ApproveAsync(cmd, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.True(commandChecked);
        var output = outWriter.ToString();
        Assert.Contains("authorized", output);
        Assert.Contains("preview", output);
    }

    [Fact]
    public async Task Approve_SendsAllowOnceCommand()
    {
        var session = CreateSession();
        var runId = "run_" + new string('a', 32);
        var approvalId = "apr_" + new string('c', 32);
        string? observedCommand = null;
        var handler = CreateFakeClient(req =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            var doc = JsonDocument.Parse(body);
            observedCommand = doc.RootElement.GetProperty("command").GetString();
            var respJson = JsonSerializer.Serialize(new
            {
                approval_id = approvalId,
                status = "authorized",
                matcher_preview = new { tool_id = "files.read" },
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respJson, Encoding.UTF8, "application/json") };
        });

        var outWriter = new StringWriter();
        var commands = new AgentCommands(session, handler, outWriter, new StringWriter());
        var cmd = new ApproveCommand(runId, approvalId, ApprovalScope.Run, false);
        var exit = await commands.ApproveAsync(cmd, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Equal("allow_once", observedCommand);
    }

    [Fact]
    public async Task Approve_FirstWriterWins_SecondGetsStale()
    {
        var session = CreateSession();
        var runId = "run_" + new string('a', 32);
        var approvalId = "apr_" + new string('e', 32);
        int callCount = 0;
        var handler = CreateFakeClient(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                var respJson = JsonSerializer.Serialize(new { approval_id = approvalId, status = "authorized", matcher_preview = new { tool_id = "files.read" } });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respJson, Encoding.UTF8, "application/json") };
            }
            else
            {
                var errJson = JsonSerializer.Serialize(new { error_code = "approval_stale", message = "already consumed" });
                return new HttpResponseMessage((HttpStatusCode)409) { Content = new StringContent(errJson, Encoding.UTF8, "application/json") };
            }
        });

        var commands = new AgentCommands(session, handler, new StringWriter(), new StringWriter());
        var cmd = new ApproveCommand(runId, approvalId, ApprovalScope.Once, false);
        var exit1 = await commands.ApproveAsync(cmd, CancellationToken.None);
        Assert.Equal(0, exit1);
        var exit2 = await commands.ApproveAsync(cmd, CancellationToken.None);
        Assert.Equal(1, exit2);
    }

    [Fact]
    public async Task Deny_Succeeds()
    {
        var session = CreateSession();
        var runId = "run_" + new string('a', 32);
        var approvalId = "apr_" + new string('f', 32);
        var handler = CreateFakeClient(req =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            var doc = JsonDocument.Parse(body);
            Assert.Equal("deny", doc.RootElement.GetProperty("command").GetString());
            var respJson = JsonSerializer.Serialize(new { approval_id = approvalId, status = "denied" });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respJson, Encoding.UTF8, "application/json") };
        });

        var outWriter = new StringWriter();
        var commands = new AgentCommands(session, handler, outWriter, new StringWriter());
        var cmd = new DenyCommand(runId, approvalId, false);
        var exit = await commands.DenyAsync(cmd, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Contains("denied", outWriter.ToString());
    }

    [Fact]
    public void Parser_Approve_DefaultsToOnce()
    {
        var runId = "run_" + new string('a', 32);
        var approvalId = "apr_" + new string('b', 32);
        var res = AgentCliParser.Parse(new[] { "agent", "approve", runId, approvalId });
        Assert.True(res.Success);
        Assert.Equal(ApprovalScope.Once, Assert.IsType<ApproveCommand>(res.Command).Scope);
    }

    [Fact]
    public void Parser_Approve_RejectsInvalidScope()
    {
        var runId = "run_" + new string('a', 32);
        var approvalId = "apr_" + new string('b', 32);
        var res = AgentCliParser.Parse(new[] { "agent", "approve", runId, approvalId, "--scope", "global" });
        Assert.False(res.Success);
        Assert.Equal("invalid_scope", res.ErrorCode);
    }

    [Fact]
    public async Task Approve_SendsAllowOnceCommand_ForProjectScope()
    {
        var session = CreateSession();
        var runId = "run_" + new string('a', 32);
        var approvalId = "apr_" + new string('a', 32).Replace('a', '9');
        string? observedCommand = null;
        var handler = CreateFakeClient(req =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            var doc = JsonDocument.Parse(body);
            observedCommand = doc.RootElement.GetProperty("command").GetString();
            var respJson = JsonSerializer.Serialize(new { approval_id = approvalId, status = "authorized" });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respJson, Encoding.UTF8, "application/json") };
        });

        var commands = new AgentCommands(session, handler, new StringWriter(), new StringWriter());
        var cmd = new ApproveCommand(runId, approvalId, ApprovalScope.Project, false);
        var exit = await commands.ApproveAsync(cmd, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Equal("allow_once", observedCommand);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _func;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = func;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_func(request));
    }
}
