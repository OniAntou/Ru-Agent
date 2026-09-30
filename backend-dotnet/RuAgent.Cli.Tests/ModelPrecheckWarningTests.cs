using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// The start-of-chat and start-of-run catalog check: one bounded warning when a models.dev
/// model id is missing or the catalog cannot be read, silence when it is present or the
/// backend's ids are not catalog-bound, and the run continues either way.
/// </summary>
public sealed class ModelPrecheckWarningTests
{
    private const string MissingModel = "missing/model-id";
    private const string PresentModel = "present/model-id";

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

    [Fact]
    public async Task Chat_warns_when_a_models_dev_model_is_missing_from_the_catalog_and_continues()
    {
        var catalogCalls = 0;
        var handler = new FakeHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Get && uri.Contains("/settings", StringComparison.Ordinal))
            {
                return Json($"{{\"status\":\"ok\",\"backend\":\"models.dev:openrouter\",\"model\":\"{MissingModel}\"}}");
            }

            if (request.Method == HttpMethod.Get && uri.Contains("/models?backend=", StringComparison.Ordinal))
            {
                catalogCalls++;
                return Json($"{{\"status\":\"ok\",\"catalog\":[{{\"id\":\"{PresentModel}\",\"supported\":true}}]}}");
            }

            return Json("{}", HttpStatusCode.NotFound);
        });

        var outWriter = new StringWriter();
        var commands = new AgentCommands(
            Session(),
            new HttpClient(handler),
            outWriter,
            new StringWriter(),
            new StringReader("/exit\n"));

        var exit = await commands.ChatAsync(
            new ChatCommand("C:\\workspace", "sess_123", "standard", Json: false),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(1, catalogCalls);
        Assert.Contains($"model_not_in_catalog: {MissingModel}", outWriter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_flattens_a_model_id_that_carries_a_newline_before_warning()
    {
        var handler = new FakeHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Get && uri.Contains("/settings", StringComparison.Ordinal))
            {
                return Json(JsonSerializer.Serialize(new
                {
                    status = "ok",
                    backend = "models.dev:openrouter",
                    model = "missing\nmodel",
                }));
            }

            if (request.Method == HttpMethod.Get && uri.Contains("/models?backend=", StringComparison.Ordinal))
            {
                return Json("{\"status\":\"ok\",\"catalog\":[]}");
            }

            return Json("{}", HttpStatusCode.NotFound);
        });

        var outWriter = new StringWriter();
        var commands = new AgentCommands(
            Session(),
            new HttpClient(handler),
            outWriter,
            new StringWriter(),
            new StringReader("/exit\n"));

        var exit = await commands.ChatAsync(
            new ChatCommand("C:\\workspace", "sess_123", "standard", Json: false),
            CancellationToken.None);

        Assert.Equal(0, exit);
        var output = outWriter.ToString();
        Assert.Contains("model_not_in_catalog: missing model", output, StringComparison.Ordinal);
        Assert.Equal(1, output.Split("model_not_in_catalog", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("missing\nmodel", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_stays_silent_when_the_model_is_in_the_catalog()
    {
        var handler = new FakeHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Get && uri.Contains("/settings", StringComparison.Ordinal))
            {
                return Json($"{{\"status\":\"ok\",\"backend\":\"models.dev:openrouter\",\"model\":\"{PresentModel}\"}}");
            }

            if (request.Method == HttpMethod.Get && uri.Contains("/models?backend=", StringComparison.Ordinal))
            {
                return Json($"{{\"status\":\"ok\",\"catalog\":[{{\"id\":\"{PresentModel}\",\"supported\":true}}]}}");
            }

            return Json("{}", HttpStatusCode.NotFound);
        });

        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var commands = new AgentCommands(
            Session(),
            new HttpClient(handler),
            outWriter,
            errWriter,
            new StringReader("/exit\n"));

        var exit = await commands.ChatAsync(
            new ChatCommand("C:\\workspace", "sess_123", "standard", Json: false),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("model_not_in_catalog", outWriter.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("model_check_unavailable", outWriter.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("model_not_in_catalog", errWriter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_prints_one_bounded_notice_when_the_catalog_cannot_be_read()
    {
        var handler = new FakeHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Get && uri.Contains("/settings", StringComparison.Ordinal))
            {
                return Json($"{{\"status\":\"ok\",\"backend\":\"models.dev:openrouter\",\"model\":\"{MissingModel}\"}}");
            }

            if (request.Method == HttpMethod.Get && uri.Contains("/models?backend=", StringComparison.Ordinal))
            {
                return Json("{\"status\":\"error\",\"message\":\"provider down\"}", HttpStatusCode.InternalServerError);
            }

            return Json("{}", HttpStatusCode.NotFound);
        });

        var outWriter = new StringWriter();
        var commands = new AgentCommands(
            Session(),
            new HttpClient(handler),
            outWriter,
            new StringWriter(),
            new StringReader("/exit\n"));

        var exit = await commands.ChatAsync(
            new ChatCommand("C:\\workspace", "sess_123", "standard", Json: false),
            CancellationToken.None);

        Assert.Equal(0, exit);
        var output = outWriter.ToString();
        Assert.Equal(1, output.Split("model_check_unavailable", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task Chat_does_not_read_a_catalog_for_a_backend_whose_ids_are_not_catalog_bound()
    {
        var catalogCalls = 0;
        var handler = new FakeHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Get && uri.Contains("/settings", StringComparison.Ordinal))
            {
                return Json("{\"status\":\"ok\",\"backend\":\"OpenAI\",\"openai_model\":\"gpt-4o\"}");
            }

            if (request.Method == HttpMethod.Get && uri.Contains("/models?backend=", StringComparison.Ordinal))
            {
                catalogCalls++;
                return Json("{\"status\":\"ok\",\"catalog\":[]}");
            }

            return Json("{}", HttpStatusCode.NotFound);
        });

        var outWriter = new StringWriter();
        var commands = new AgentCommands(
            Session(),
            new HttpClient(handler),
            outWriter,
            new StringWriter(),
            new StringReader("/exit\n"));

        var exit = await commands.ChatAsync(
            new ChatCommand("C:\\workspace", "sess_123", "standard", Json: false),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(0, catalogCalls);
        Assert.DoesNotContain("model_not_in_catalog", outWriter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_warns_when_a_models_dev_model_is_missing_from_the_catalog_and_continues()
    {
        var sessionId = new string('b', 32);
        var runId = "run_" + new string('a', 32);
        var handler = new FakeHandler(async request =>
        {
            var uri = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Get && uri.Contains("/settings", StringComparison.Ordinal))
            {
                return Json($"{{\"status\":\"ok\",\"backend\":\"models.dev:openrouter\",\"model\":\"{MissingModel}\"}}");
            }

            if (request.Method == HttpMethod.Get && uri.Contains("/models?backend=", StringComparison.Ordinal))
            {
                return Json($"{{\"status\":\"ok\",\"catalog\":[{{\"id\":\"{PresentModel}\",\"supported\":true}}]}}");
            }

            if (request.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var token = document.RootElement.GetProperty("snapshot_token").GetString();
                return Json(JsonSerializer.Serialize(new { session_id = sessionId, preset = "standard", version = 1, snapshot_token = token }));
            }

            if (request.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs", StringComparison.Ordinal))
            {
                return Json(
                    JsonSerializer.Serialize(new
                    {
                        run_id = runId,
                        session_id = sessionId,
                        workspace_id = "ws_test",
                        status = "running",
                        preset = "standard",
                        created_at = DateTimeOffset.UtcNow.ToString("O"),
                        updated_at = DateTimeOffset.UtcNow.ToString("O"),
                    }),
                    HttpStatusCode.Created);
            }

            if (request.Method == HttpMethod.Get && uri.Contains("/events", StringComparison.Ordinal))
            {
                var sse =
                    $"id: 1\ndata: {{\"protocol_version\":1,\"event_id\":\"evt_test\",\"run_id\":\"{runId}\",\"session_id\":\"{sessionId}\",\"sequence\":1,\"event_type\":\"run.completed\",\"occurred_at\":\"2026-09-24T00:00:00Z\",\"status\":\"succeeded\",\"safe_summary\":\"done\"}}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
                };
            }

            return Json("{}", HttpStatusCode.NotFound);
        });

        var outWriter = new StringWriter();
        var commands = new AgentCommands(Session(), new HttpClient(handler), outWriter, new StringWriter());

        var exit = await commands.RunAsync(
            new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "go", sessionId, "standard", false),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains($"model_not_in_catalog: {MissingModel}", outWriter.ToString(), StringComparison.Ordinal);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
            _handler = request => Task.FromResult(handler(request));

        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) =>
            _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _handler(request);
    }
}
