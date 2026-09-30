using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Cli;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// `agent chat --json` owns stdout: slash-command surfaces render on stderr, picker-only
/// commands refuse instead of prompting, and a run that reaches an approval request ends
/// the turn with a bounded stderr notice while the run stays waiting for
/// `ru agent approve` — stdout never carries a bare sentence or a control byte.
/// </summary>
public sealed class ChatJsonStdoutPurityTests
{
    private static readonly string RunId = "run_" + new string('a', 32);
    private const string SessionId = "sess_123";
    private static readonly string ApprovalId = "ap_" + new string('d', 32);

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

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Settings() =>
        Json("{\"status\":\"ok\",\"backend\":\"OpenAI\",\"openai_model\":\"gpt-4o\",\"has_openai_api_key\":true}");

    private static List<string> AssertStdoutIsNdjson(string stdout)
    {
        var lines = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal(1, document.RootElement.GetProperty("v").GetInt32());
            Assert.True(document.RootElement.TryGetProperty("type", out _), line);
        }

        return lines;
    }

    private static AgentCommands CreateCommands(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        StringWriter outWriter,
        StringWriter errWriter,
        TextReader inReader) =>
        new(Session(), new HttpClient(new FakeHandler(respond)), outWriter, errWriter, inReader);

    [Fact]
    public async Task ChatAsync_Json_SlashCommand_HumanTextGoesToStderr_StdoutStaysNdjson()
    {
        var calls = new List<string>();
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var reader = new ScriptedThrowingReader("/status\n/exit\n");
        var commands = CreateCommands(request =>
        {
            calls.Add(request.Method.ToString());
            return request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/settings", StringComparison.Ordinal)
                ? Settings()
                : Json("{}", HttpStatusCode.NotFound);
        }, outWriter, errWriter, reader);

        var exit = await commands.ChatAsync(
            new ChatCommand("C:\\workspace", SessionId, "standard", Json: true),
            CancellationToken.None);

        Assert.Equal(0, exit);
        AssertStdoutIsNdjson(outWriter.ToString());
        Assert.DoesNotContain("╭─ status", outWriter.ToString(), StringComparison.Ordinal);
        Assert.Contains("╭─ status", errWriter.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("POST", calls);
        // Exactly the two scripted prompt lines: the slash handler never reads stdin,
        // and the reader throws if anything asks for a line beyond the script.
        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public async Task ChatAsync_Json_InteractiveSlash_DoesNotPrompt()
    {
        var posts = new List<string>();
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var reader = new ScriptedThrowingReader("/backend\n/exit\n");
        var commands = CreateCommands(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posts.Add(request.RequestUri!.AbsolutePath);
                return Json("{\"status\":\"ok\"}");
            }

            return request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/settings", StringComparison.Ordinal)
                ? Settings()
                : Json("{}", HttpStatusCode.NotFound);
        }, outWriter, errWriter, reader);

        var exit = await commands.ChatAsync(
            new ChatCommand("C:\\workspace", SessionId, "standard", Json: true),
            CancellationToken.None);

        Assert.Equal(0, exit);
        // The bare picker form refuses instead of opening a picker or mutating settings.
        Assert.Empty(posts);
        AssertStdoutIsNdjson(outWriter.ToString());
        Assert.DoesNotContain("╭─ backend", outWriter.ToString(), StringComparison.Ordinal);
        Assert.Contains("interactive_required", errWriter.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public async Task ChatAsync_Json_ApprovalRequest_DoesNotPrompt_AndPointAtAgentApprove()
    {
        var mutatingCalls = new List<string>();
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var reader = new ScriptedThrowingReader("hello\n");
        var commands = CreateCommands(request =>
        {
            var uri = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Post
                && (uri.Contains("/approvals/") || uri.EndsWith("/cancel", StringComparison.Ordinal)))
            {
                mutatingCalls.Add(uri);
                return Json("{}");
            }

            if (request.Method == HttpMethod.Get && uri.Contains("/settings"))
            {
                return Settings();
            }

            if (request.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies", StringComparison.Ordinal))
            {
                return Json(JsonSerializer.Serialize(new { session_id = SessionId, preset = "standard", version = 1 }));
            }

            if (request.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs", StringComparison.Ordinal))
            {
                return Json(JsonSerializer.Serialize(new
                {
                    run_id = RunId,
                    session_id = SessionId,
                    workspace_id = "ws_" + new string('c', 32),
                    status = "running",
                    preset = "standard",
                    created_at = "2026-09-30T00:00:00Z",
                    updated_at = "2026-09-30T00:00:00Z",
                }), HttpStatusCode.Created);
            }

            if (request.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                var sse =
                    $"id: 1\ndata: {{\"protocol_version\":1,\"event_id\":\"evt_{new string('e', 32)}\",\"run_id\":\"{RunId}\",\"session_id\":\"{SessionId}\",\"sequence\":1,\"event_type\":\"run.state_changed\",\"occurred_at\":\"2026-09-30T00:00:00Z\",\"status\":\"running\",\"safe_summary\":\"started\"}}\n\n" +
                    $"id: 2\ndata: {{\"protocol_version\":1,\"event_id\":\"evt_{new string('f', 32)}\",\"run_id\":\"{RunId}\",\"session_id\":\"{SessionId}\",\"sequence\":2,\"event_type\":\"approval.requested\",\"occurred_at\":\"2026-09-30T00:00:01Z\",\"status\":\"waiting_approval\",\"safe_summary\":\"external_directory_required\",\"approval_id\":\"{ApprovalId}\"}}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
                };
            }

            return Json("{}", HttpStatusCode.NotFound);
        }, outWriter, errWriter, reader);

        var exit = await commands.ChatAsync(
            new ChatCommand("C:\\workspace", SessionId, "standard", Json: true),
            CancellationToken.None);

        Assert.Equal(0, exit);
        var lines = AssertStdoutIsNdjson(outWriter.ToString());
        Assert.Contains(lines, line => line.Contains("\"event_type\":\"approval.requested\"", StringComparison.Ordinal));
        // The approval is neither auto-approved nor auto-denied, and the run is not cancelled.
        Assert.Empty(mutatingCalls);
        Assert.Contains(ApprovalId, errWriter.ToString(), StringComparison.Ordinal);
        Assert.Contains($"ru agent approve {RunId} {ApprovalId}", errWriter.ToString(), StringComparison.Ordinal);
        // Only the one scripted prompt line was read: the approval itself never reads stdin
        // and the chat exits instead of asking for another prompt the consumer must not answer.
        Assert.Equal(1, reader.ReadCount);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _func;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = func;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_func(request));
    }

    /// <summary>
    /// Serves the scripted lines and throws when asked for one more. Any unexpected stdin
    /// read therefore fails the test instead of silently returning EOF.
    /// </summary>
    private sealed class ScriptedThrowingReader : StringReader
    {
        private readonly Queue<string> _lines;

        public ScriptedThrowingReader(string script)
            : base(script)
        {
            _lines = new Queue<string>(script
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r')));
        }

        public int ReadCount { get; private set; }

        public override string? ReadLine() => Next();

        public override Task<string?> ReadLineAsync() => Task.FromResult(Next());

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Next());

        private string? Next()
        {
            ReadCount++;
            if (_lines.Count == 0)
            {
                throw new InvalidOperationException("stdin was read beyond the scripted input");
            }

            return _lines.Dequeue();
        }
    }
}
