using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Cli.Images;
using RuAgent.Cli.TerminalGui;
using RuAgent.Client;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatHostReconnectTests
{
    private const string SessionId = "sess_existing";
    private static readonly string RunId = "run_" + new string('a', 32);
    private static readonly PublicHostSession Replacement = ChatTestSession.Value with
    {
        BaseUri = new Uri("http://127.0.0.1:2/"),
        BearerToken = "replacement-test-token",
    };

    [Theory]
    [InlineData("model-a", true)]
    [InlineData("model-b", false)]
    public async Task Reconnect_rechecks_settings_before_submitting_in_the_same_session(string replacementModel, bool shouldRun)
    {
        using var host = new Host(replacementModel);
        using var http = new HttpClient(host);
        using var output = new StringWriter();
        using var errors = new StringWriter();
        using var input = new PromptAfterStartup(() => host.Disconnected = true);
        var reconnects = 0;
        using var commands = new AgentCommands(ChatTestSession.Value, http, output, errors, input)
        {
            ReconnectHost = _ => { reconnects++; return Task.FromResult(Replacement); },
        };

        Assert.Equal(0, await commands.ChatAsync(new ChatCommand(Path.GetTempPath(), SessionId, "standard", Json: true), default));

        Assert.Equal(1, reconnects);
        var disconnected = Assert.Single(host.AfterDisconnect, r => r.Port == 1);
        Assert.Equal((HttpMethod.Get, "/settings"), (disconnected.Method, disconnected.Path));
        Assert.All(host.AfterDisconnect.Where(r => r.Port == 2), r => Assert.Equal(Replacement.BearerToken, r.Bearer));
        Assert.DoesNotContain(host.AfterDisconnect, r => r.Path == "/sessions/new");
        Assert.DoesNotContain("submission_settings_unavailable", errors.ToString());
        if (shouldRun)
        {
            Assert.Equal(1, host.AfterDisconnect.Count(r => r.Method == HttpMethod.Post && r.Path == "/agent/v1/runs"));
            Assert.Equal(SessionId, host.Submission.GetProperty("session_id").GetString());
            Assert.Equal("continue this chat", host.Submission.GetProperty("prompt").GetString());
            Assert.False(string.IsNullOrWhiteSpace(host.Submission.GetProperty("idempotency_key").GetString()));
            Assert.Contains("\"status\":\"completed\"", output.ToString());
        }
        else
        {
            Assert.DoesNotContain(host.AfterDisconnect, r => r.Method == HttpMethod.Post);
            Assert.Contains("submission_context_changed", errors.ToString());
        }
    }

    [Fact]
    public async Task Native_chat_keeps_the_image_and_pinned_model_when_the_host_restarts()
    {
        using var host = new Host("model-a");
        using var http = new HttpClient(host);
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        using var pending = new ChatSubmissionState();
        var image = new CliImageAttachment("fixture.png", "image/png", "aW1hZ2U=", 1, 1, 5);
        pending.Add(image);
        using var commands = new AgentCommands(ChatTestSession.Value, http, TextWriter.Null, TextWriter.Null)
        {
            ReconnectHost = _ => Task.FromResult(Replacement),
        };
        var writer = new TgTranscriptWriter(app, window.Transcript);
        var path = Path.GetTempPath();
        var bootstrap = new AgentCommands.ChatBootstrapContext(pending, writer, path, path, SessionId,
            "OpenAI", "model-a", "", null, ChatPresentationMode.Native, true, writer, window, app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var submitted = false;
        var completed = false;
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            if (app.TopRunnableView is not TgChatWindow) return true;
            if (!submitted)
            {
                submitted = true;
                host.Disconnected = true;
                window.FocusComposer();
                window.Composer.Input.Text = "describe the attachment";
                app.InjectKey(Key.Enter);
                return true;
            }

            if (!host.AfterDisconnect.Any(r => r.Path == $"/agent/v1/runs/{RunId}/events") || window.Hints.IsRunning)
                return true;
            completed = true;
            window.FocusComposer();
            window.Composer.Input.Text = "/exit";
            app.InjectKey(Key.Enter);
            return false;
        });

        Assert.Equal(0, await commands.RunPreparedChatAsync(new ChatCommand(path, SessionId, "standard", false), bootstrap, deadline.Token));
        Assert.True(completed);
        var sent = Assert.Single(host.AfterDisconnect, r => r.Method == HttpMethod.Post && r.Path == "/agent/v1/runs/selected");
        Assert.Equal(2, sent.Port);
        Assert.Equal(Replacement.BearerToken, sent.Bearer);
        Assert.Equal(SessionId, host.Submission.GetProperty("session_id").GetString());
        Assert.Equal("describe the attachment", host.Submission.GetProperty("prompt").GetString());
        Assert.Equal("model-a", host.Submission.GetProperty("provider_selection").GetProperty("model").GetString());
        Assert.Equal(image.ToDataUrl(), Assert.Single(host.Submission.GetProperty("image_attachments").EnumerateArray()).GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_recovery_or_settings_rejection_holds_the_prompt_without_writes(bool rejected)
    {
        using var host = new Host("model-a") { RejectSettings = rejected };
        using var http = new HttpClient(host);
        using var errors = new StringWriter();
        using var input = new PromptAfterStartup(() => host.Disconnected = true);
        var reconnects = 0;
        using var commands = new AgentCommands(ChatTestSession.Value, http, TextWriter.Null, errors, input)
        {
            ReconnectHost = _ =>
            {
                reconnects++;
                throw new AgentApiException("public_host_start_failed", "Synthetic startup failure.");
            },
        };

        Assert.Equal(0, await commands.ChatAsync(new ChatCommand(Path.GetTempPath(), SessionId, "standard", Json: true), default));

        Assert.Equal(rejected ? 0 : 1, reconnects);
        Assert.DoesNotContain(host.AfterDisconnect, r => r.Method == HttpMethod.Post);
        Assert.Contains("submission_settings_unavailable", errors.ToString());
    }

    private sealed class PromptAfterStartup(Action beforePrompt) : StringReader("continue this chat\n/exit\n")
    {
        private bool _started;

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (!_started)
            {
                _started = true;
                beforePrompt();
            }
            return base.ReadLineAsync(cancellationToken);
        }
    }

    private sealed class Host(string replacementModel) : HttpMessageHandler
    {
        public bool Disconnected { get; set; }
        public bool RejectSettings { get; init; }
        public JsonElement Submission { get; private set; }
        public ConcurrentQueue<(int Port, HttpMethod Method, string Path, string? Bearer)> AfterDisconnect { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            if (Disconnected)
            {
                AfterDisconnect.Enqueue((uri.Port, request.Method, path, request.Headers.Authorization?.Parameter));
                if (uri.Port == 1)
                {
                    if (RejectSettings) return Json(new { status = "error" }, HttpStatusCode.Forbidden);
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "Synthetic idle shutdown.");
                }
            }

            if (path == "/settings")
                return Json(new { status = "ok", backend = "OpenAI", openai_model = uri.Port == 1 ? "model-a" : replacementModel });
            if (path == "/agent/v1/policies" && request.Method == HttpMethod.Post)
                return Json(new { session_id = SessionId, preset = "standard", version = 1 });
            if (request.Method == HttpMethod.Post && path is "/agent/v1/runs" or "/agent/v1/runs/selected")
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Submission = document.RootElement.Clone();
                return Json(Run("running"), HttpStatusCode.Created);
            }
            if (path == $"/agent/v1/runs/{RunId}") return Json(Run("completed"));
            if (path == $"/agent/v1/runs/{RunId}/events")
            {
                var frame = JsonSerializer.Serialize(new
                {
                    protocol_version = 1, event_id = "evt_" + new string('b', 32),
                    run_id = RunId, session_id = SessionId, sequence = 1,
                    event_type = "run.state_changed", occurred_at = "2026-11-06T00:00:00Z", status = "completed",
                    safe_summary = "done",
                });
                return new(HttpStatusCode.OK) { Content = new StringContent($"id: 1\ndata: {frame}\n\n", Encoding.UTF8, "text/event-stream") };
            }

            return Json(new { status = "ok", items = Array.Empty<object>(), models = Array.Empty<object>() });
        }

        private static object Run(string status) => new
        {
            run_id = RunId, session_id = SessionId, workspace_id = "ws_" + new string('c', 32),
            status, preset = "standard", last_sequence = 1,
            created_at = "2026-11-06T00:00:00Z", updated_at = "2026-11-06T00:00:00Z",
            terminal_at = status == "completed" ? "2026-11-06T00:00:00Z" : null,
        };

        private static HttpResponseMessage Json(object body, HttpStatusCode code = HttpStatusCode.OK) =>
            new(code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
