using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class PolicyCommandTests
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

    private static HttpClient CreateFakeClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var fake = new FakeHandler(handler);
        return new HttpClient(fake);
    }

    [Fact]
    public async Task RunFullAccessPresetSendsInteractiveConfirmationHeader()
    {
        var sessionId = new string('8', 32);
        var runId = "run_" + new string('9', 32);
        var policySeen = false;
        var handler = CreateFakeClient(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                Assert.True(req.Headers.Contains("X-Interactive-Confirmed"));
                Assert.Contains("true", req.Headers.GetValues("X-Interactive-Confirmed"));
                using var document = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
                Assert.Equal("full_access", document.RootElement.GetProperty("preset").GetString());
                Assert.Equal(0, document.RootElement.GetProperty("expected_version").GetInt64());
                policySeen = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { session_id = sessionId, preset = "full_access", version = 1 }), Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
            {
                var body = JsonSerializer.Serialize(new { run_id = runId, session_id = sessionId, workspace_id = "ws_test", status = "running", preset = "full_access", created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O") });
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                var sse = $"id: 1\ndata: {{\"protocol_version\":1,\"event_id\":\"evt_test\",\"run_id\":\"{runId}\",\"session_id\":\"{sessionId}\",\"sequence\":1,\"event_type\":\"run.completed\",\"occurred_at\":\"2026-09-24T00:00:00Z\",\"status\":\"succeeded\",\"safe_summary\":\"done\"}}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });

        var commands = new AgentCommands(CreateSession(), handler, new StringWriter(), new StringWriter());
        var exit = await commands.RunAsync(
            new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "full access", sessionId, "full_access", true, ConfirmFullAccess: true),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(policySeen);
    }

    [Fact]
    public void PolicyJsonCodec_RejectsUnknownVersion()
    {
        var json = "{\"version\":99,\"rules\":[]}";
        var ex = Assert.Throws<PolicyJsonException>(() => PolicyJsonCodec.Parse(json));
        Assert.Equal("unknown_version", ex.ErrorCode);
    }

    [Fact]
    public void PolicyJsonCodec_RejectsUnknownField()
    {
        var json = "{\"version\":1,\"rules\":[],\"extra\":\"field\"}";
        var ex = Assert.Throws<PolicyJsonException>(() => PolicyJsonCodec.Parse(json));
        Assert.Equal("unknown_field", ex.ErrorCode);
    }

    [Fact]
    public void PolicyJsonCodec_RejectsUnknownRuleField()
    {
        var json = "{\"version\":1,\"rules\":[{\"scope\":\"global\",\"decision\":\"allow\",\"matcher\":{},\"unknown\":\"x\"}]}";
        var ex = Assert.Throws<PolicyJsonException>(() => PolicyJsonCodec.Parse(json));
        Assert.Equal("unknown_field", ex.ErrorCode);
    }

    [Fact]
    public void PolicyJsonCodec_RejectsUnknownMatcherField()
    {
        var json = "{\"version\":1,\"rules\":[{\"scope\":\"global\",\"decision\":\"allow\",\"matcher\":{\"unknown_field\":\"x\"}}]}";
        var ex = Assert.Throws<PolicyJsonException>(() => PolicyJsonCodec.Parse(json));
        Assert.Equal("unknown_field", ex.ErrorCode);
    }

    [Fact]
    public void PolicyJsonCodec_NormalizesAndDisplaysMatchers()
    {
        var json = "{\"version\":1,\"rules\":[{\"scope\":\"global\",\"decision\":\"allow\",\"matcher\":{\"tool_id\":\"read\",\"canonical_path_pattern\":\"a.txt\",\"operation_class\":\"read\"}}]}";
        var file = PolicyJsonCodec.Parse(json);
        var display = PolicyJsonCodec.NormalizeAndDisplay(file);
        Assert.Contains("allow", display);
        Assert.Contains("read", display);
        Assert.Contains("a.txt", display);
        // Normalized should be sorted keys
        Assert.Contains("canonical_path_pattern", display);
    }

    [Fact]
    public async Task PolicyList_WithFakeHttp_Succeeds()
    {
        var session = CreateSession();
        var handler = CreateFakeClient(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/agent/v1/policies"))
            {
                var respJson = JsonSerializer.Serialize(new { items = new[] { new { rule_id = "rule_" + new string('a', 32), scope = "global", decision = "allow", matcher = new { tool_id = "read" } } } });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respJson, Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var commands = new AgentCommands(session, handler, outWriter, errWriter);
        var cmd = new PolicyListCommand(Global: true, WorkspacePath: null, Json: false);
        var exit = await commands.PolicyListAsync(cmd, CancellationToken.None);
        Assert.Equal(0, exit);
        var output = outWriter.ToString();
        Assert.Contains("policies:", output);
    }

    [Fact]
    public async Task PolicyImport_WithInteractiveConfirmation_Succeeds()
    {
        var session = CreateSession();
        var tempFile = Path.GetTempFileName();
        var policyJson = "{\"version\":1,\"rules\":[{\"scope\":\"global\",\"decision\":\"allow\",\"matcher\":{\"tool_id\":\"read\",\"canonical_path_pattern\":\"src/app.ts\"}}]}";
        File.WriteAllText(tempFile, policyJson);

        bool headerChecked = false;
        var handler = CreateFakeClient(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                // Must have interactive header for global
                Assert.True(req.Headers.Contains("X-Interactive-Confirmed"));
                var values = req.Headers.GetValues("X-Interactive-Confirmed");
                Assert.Contains("true", values);
                headerChecked = true;
                var body = await req.Content!.ReadAsStringAsync();
                var doc = JsonDocument.Parse(body);
                Assert.Equal("global", doc.RootElement.GetProperty("scope").GetString());
                var respJson = JsonSerializer.Serialize(new { rule_id = "rule_" + new string('b', 32), scope = "global", decision = "allow" });
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(respJson, Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var inReader = new StringReader("y\n");
        var commands = new AgentCommands(session, handler, outWriter, errWriter, inReader);
        var cmd = new PolicyImportCommand(Global: true, FilePath: tempFile, Json: false);
        var exit = await commands.PolicyImportAsync(cmd, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.True(headerChecked);
        var output = outWriter.ToString();
        Assert.Contains("Normalized", output);
        Assert.Contains("imported", output);
        File.Delete(tempFile);
    }

    [Fact]
    public async Task PolicyImport_WithoutConfirmation_Cancels()
    {
        var session = CreateSession();
        var tempFile = Path.GetTempFileName();
        var policyJson = "{\"version\":1,\"rules\":[{\"scope\":\"global\",\"decision\":\"allow\",\"matcher\":{\"tool_id\":\"read\"}}]}";
        File.WriteAllText(tempFile, policyJson);

        var handler = CreateFakeClient(async req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var inReader = new StringReader("n\n");
        var commands = new AgentCommands(session, handler, outWriter, errWriter, inReader);
        var cmd = new PolicyImportCommand(true, tempFile, false);
        var exit = await commands.PolicyImportAsync(cmd, CancellationToken.None);
        Assert.Equal(1, exit);
        var err = errWriter.ToString();
        Assert.Contains("cancelled", err);
        File.Delete(tempFile);
    }

    [Fact]
    public async Task PolicyImport_RejectsUnknownVersion_BeforeHttp()
    {
        var session = CreateSession();
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "{\"version\":2,\"rules\":[]}");
        var handler = CreateFakeClient(async req => throw new Exception("Should not call HTTP for invalid version"));
        var commands = new AgentCommands(session, handler, new StringWriter(), new StringWriter(), new StringReader("y\n"));
        var cmd = new PolicyImportCommand(true, tempFile, false);
        var exit = await commands.PolicyImportAsync(cmd, CancellationToken.None);
        Assert.Equal(1, exit);
        File.Delete(tempFile);
    }

    [Fact]
    public async Task PolicyRemove_WithConfirmation_Succeeds()
    {
        var session = CreateSession();
        var ruleId = "rule_" + new string('c', 32);
        bool headerChecked = false;
        var handler = CreateFakeClient(async req =>
        {
            if (req.Method == HttpMethod.Delete && req.RequestUri!.ToString().Contains(ruleId))
            {
                Assert.True(req.Headers.Contains("X-Interactive-Confirmed"));
                headerChecked = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"deleted\":true}", Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var inReader = new StringReader("yes\n");
        var commands = new AgentCommands(session, handler, outWriter, errWriter, inReader);
        var cmd = new PolicyRemoveCommand(ruleId, false);
        var exit = await commands.PolicyRemoveAsync(cmd, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.True(headerChecked);
        Assert.Contains("removed", outWriter.ToString());
    }

    [Fact]
    public void Parser_PolicyImport_GlobalViaFileOnly()
    {
        var res = AgentCliParser.Parse(new[] { "agent", "policy", "import", "--global", "myfile.json" });
        Assert.True(res.Success);
        var cmd = Assert.IsType<PolicyImportCommand>(res.Command);
        Assert.Equal("myfile.json", cmd.FilePath);
        Assert.True(cmd.Global);
    }

    [Fact]
    public void Parser_PolicyImport_WithoutGlobalFails()
    {
        var res = AgentCliParser.Parse(new[] { "agent", "policy", "import", "myfile.json" });
        Assert.False(res.Success);
        Assert.Equal("missing_scope", res.ErrorCode);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _func;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = req => Task.FromResult(func(req));
        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> func) => _func = func;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _func(request);
    }
}
