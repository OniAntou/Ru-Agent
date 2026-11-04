using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiContextWindowWorkflowTests
{
    [Theory]
    [InlineData("chat_completions", true)]
    [InlineData("openai_responses", true)]
    [InlineData("anthropic_messages", false)]
    public async Task Header_tracks_the_current_request_instead_of_run_billing_and_ignores_late_estimates(string protocol, bool includesCache)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var page = TgChatWindow.Build();
        using var host = new ContextHost(protocol, includesCache);
        using var http = new HttpClient(host);
        using var commands = new AgentCommands(ChatRewindWorkflowTests.Host.Session, http, TextWriter.Null, TextWriter.Null);
        var writer = new TgTranscriptWriter(app, page.Transcript);
        var path = Path.GetTempPath();
        var bootstrap = new AgentCommands.ChatBootstrapContext(new ChatSubmissionState(), writer, path, path, "session_1",
            "OpenAI", "gpt-4o", "high", null, ChatPresentationMode.Native, true, writer, page, app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stage = 0;
        Exception? failure = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            try
            {
                if (app.TopRunnableView is not TgChatWindow) return true;
                if (stage == 0)
                {
                    page.FocusComposer(); page.Composer.Input.Text = "Check context";
                    app.InjectKey(Key.Enter); stage++;
                }
                else if (page.Transcript.Lines.Any(line => line.Text.Contains("0 tools")))
                {
                    Assert.Equal(52_000, page.Header.ContextTokens);
                    Assert.Equal(40.625, page.Header.ContextPercent);
                    Assert.False(page.Header.ContextIsEstimated);
                    Assert.Equal(2, host.UsageReads);
                    page.Composer.Input.Text = "/exit"; app.InjectKey(Key.Enter); stage++;
                    return false;
                }
            }
            catch (Exception error) { failure = error; app.RequestStop(); return false; }
            return !deadline.IsCancellationRequested;
        });
        Assert.Equal(0, await commands.RunPreparedChatAsync(new ChatCommand(path, "session_1", "standard", false), bootstrap, deadline.Token));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(stage == 2, $"stage={stage}; requests={string.Join(',', host.Requests)}; transcript={string.Join('|', page.Transcript.Lines.Select(row => row.Text))}");
    }

    private sealed class ContextHost(string protocol, bool includesCache) : HttpMessageHandler
    {
        private const string Run = "run_11111111111111111111111111111111";
        private const string StepA = "step_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string StepB = "step_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        internal int UsageReads;
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Requests = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Enqueue(path);
            if (path == "/settings") return Json(new { status = "ok", backend = "OpenAI", openai_model = "gpt-4o" });
            if (path.EndsWith("/preset")) return request.Method == HttpMethod.Get
                ? Json(new { session_id = "session_1", preset = (string?)null, version = 0 })
                : Json(new { session_id = "session_1", preset = "standard", version = 1 });
            if (path == "/agent/v1/policies") return Json(new { session_id = "session_1", preset = "standard", version = 1 });
            if (path == "/agent/v1/runs/selected")
            {
                _ = await request.Content!.ReadAsStringAsync(ct);
                return Json(new { run_id = Run, session_id = "session_1", workspace_id = "ws_" + new string('a', 32),
                    status = "running", preset = "standard", created_at = "2026-11-02T00:00:00Z", updated_at = "2026-11-02T00:00:00Z" }, HttpStatusCode.Created);
            }
            if (path.EndsWith("/events")) return new(HttpStatusCode.OK)
            { Content = new StringContent(Frames(), Encoding.UTF8, "text/event-stream") };
            if (path.Contains("/payloads/", StringComparison.Ordinal))
            {
                UsageReads++;
                var input = path.EndsWith(new string('1', 32), StringComparison.Ordinal) ? 50_000 : 52_000;
                return Json(new { run_id = Run, payload_id = path.Split('/').Last(), kind = "modelusage", encoding = "json",
                    content = JsonSerializer.Serialize(new { input_tokens = includesCache ? input : input - 1000,
                        cache_read_input_tokens = 1000, output_tokens = 500, total_tokens = input + 500, input_tokens_include_cache = includesCache }) });
            }
            if (path == "/agent/v1/runs/" + Run) return Json(new
            {
                run_id = Run, session_id = "session_1", workspace_id = "ws_" + new string('a', 32), status = "completed", preset = "standard",
                policy_snapshot_version = 1, policy_snapshot_sha256 = new string('a', 64), version = 1, last_sequence = 5,
                created_at = "2026-11-02T00:00:00Z", updated_at = "2026-11-02T00:00:01Z", terminal_at = "2026-11-02T00:00:01Z",
                provider_backend = "fixture", provider_model = "fixture-model", provider_protocol = protocol,
                context_window_tokens = 128_000, output_token_reserve = 8_000,
            });
            if (path.EndsWith("/cancel")) return Json(new { run_id = Run, status = "cancelled", version = 2 });
            if (path.StartsWith("/sessions/", StringComparison.Ordinal)) return Json(new
            { status = "ok", session = new { id = "session_1", title = "Context", messages = Array.Empty<object>() } });
            return Json(new { status = "ok", items = Array.Empty<object>(), providers = Array.Empty<object>(), models = Array.Empty<object>() });
        }

        private static string Frames()
        {
            var text = new StringBuilder();
            void Durable(int sequence, string type, string step, long? input = null, long? output = null, string? payload = null) =>
                text.Append($"id: {sequence}\ndata: ").Append(JsonSerializer.Serialize(new
                {
                    protocol_version = 1, event_id = "evt_" + sequence, run_id = Run, session_id = "session_1", sequence,
                    event_type = type, occurred_at = "2026-11-02T00:00:00Z", status = type == "run.state_changed" ? "completed" : "running",
                    safe_summary = "ok", model_step_id = step, payload_ref = payload,
                    input_tokens = input, output_tokens = output, total_tokens = input is null ? (long?)null : input + output,
                })).Append("\n\n");
            void Transient(int sequence, string kind, object body) =>
                text.Append($"event: transient\nid: t{sequence}\ndata: ")
                    .Append(JsonSerializer.Serialize(new { kind, data = JsonSerializer.Serialize(body) })).Append("\n\n");
            void Estimate(int sequence, string step, int input) => Transient(sequence, "agent.usage_estimate",
                new { model_step_id = step, estimated_input_tokens = input, max_prompt_tokens = 120_000 });
            Durable(1, "model.step_started", StepA);
            Estimate(1, StepA, 80_000);
            Durable(2, "model.usage_recorded", StepA, 50_000, 500, "payload_" + new string('1', 32));
            Estimate(2, StepA, 80_000);
            Durable(3, "model.step_started", StepB, 50_000, 500);
            Estimate(3, StepB, 55_000);
            Durable(4, "model.usage_recorded", StepB, 102_000, 1000, "payload_" + new string('2', 32));
            Estimate(4, StepA, 80_000);
            Transient(5, "assistant.delta", new { model_step_id = StepB, text = "Context fixture complete.\n" });
            Durable(5, "run.state_changed", StepB, 102_000, 1000);
            return text.ToString();
        }

        private static HttpResponseMessage Json(object body, HttpStatusCode code = HttpStatusCode.OK) =>
            new(code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
