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

public sealed class ChatConnectionTests
{
    private const string SessionId = "sess_existing";
    private static readonly string RunId = "run_" + new string('a', 32);
    private static readonly PublicHostSession Replacement = ChatTestSession.Value with
    {
        BaseUri = new Uri("http://127.0.0.1:2/"),
        BearerToken = "replacement-token",
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Settings_failure_holds_the_prompt_without_reconnecting_or_writing(bool rejected)
    {
        using var host = new Host { RejectSettings = rejected };
        using var http = new HttpClient(host);
        using var errors = new StringWriter();
        using var input = new ScriptedInput("start task\n/exit\n", line =>
        {
            if (line == 1) host.Disconnected = true;
        });
        var recoveryAttempts = 0;
        using var commands = new AgentCommands(ChatTestSession.Value, http, TextWriter.Null, errors, input)
        {
            ResolveSubmissionHost = _ =>
            {
                recoveryAttempts++;
                throw new HttpRequestException("Synthetic Host startup failure.");
            },
        };

        Assert.Equal(0, await commands.ChatAsync(new ChatCommand(Path.GetTempPath(), SessionId, "standard", Json: true), default));

        var read = Assert.Single(host.AfterDisconnect, request => request.Path == "/settings");
        Assert.Equal((1, HttpMethod.Get), (read.Port, read.Method));
        Assert.All(host.AfterDisconnect, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Contains("submission_settings_unavailable", errors.ToString());
        Assert.Equal(rejected ? 0 : 1, recoveryAttempts);
    }

    [Theory]
    [InlineData("model-a", false, 1)]
    [InlineData("model-b", false, 0)]
    [InlineData("model-a", true, 0)]
    public async Task Submission_recovers_once_and_starts_only_with_validated_settings(
        string replacementModel, bool replacementUnavailable, int expectedRuns)
    {
        using var host = new Host { ReplacementModel = replacementModel, ReplacementUnavailable = replacementUnavailable };
        using var http = new HttpClient(host);
        using var output = new StringWriter();
        using var errors = new StringWriter();
        using var input = new ScriptedInput("start task\n/exit\n", line =>
        {
            if (line == 1) host.Disconnected = true;
        });
        var recoveryAttempts = 0;
        using var commands = new AgentCommands(ChatTestSession.Value, http, output, errors, input)
        {
            ResolveSubmissionHost = _ =>
            {
                recoveryAttempts++;
                return Task.FromResult(Replacement);
            },
        };

        Assert.Equal(0, await commands.ChatAsync(new ChatCommand(Path.GetTempPath(), SessionId, "standard", Json: true), default));

        Assert.Equal(1, recoveryAttempts);
        Assert.Equal(expectedRuns, host.Created);
        Assert.Collection(host.AfterDisconnect.Where(request => request.Path == "/settings"),
            read => Assert.Equal((1, HttpMethod.Get), (read.Port, read.Method)),
            read => Assert.Equal((2, HttpMethod.Get), (read.Port, read.Method)));
        Assert.All(host.AfterDisconnect.Where(request => request.Port == 1), request => Assert.Equal(HttpMethod.Get, request.Method));
        if (expectedRuns == 0)
        {
            Assert.All(host.AfterDisconnect, request => Assert.Equal(HttpMethod.Get, request.Method));
            Assert.Contains(replacementUnavailable ? "submission_settings_unavailable" : "submission_context_changed", errors.ToString());
        }
        else
        {
            Assert.Equal([0L], host.Cursors);
            Assert.Contains("\"safe_summary\":\"finished\"", output.ToString());
            Assert.DoesNotContain("submission_settings_unavailable", errors.ToString());
        }
    }

    [Fact]
    public async Task Native_chat_sends_the_same_session_and_image_after_idle_shutdown()
    {
        using var host = new Host();
        using var http = new HttpClient(host);
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        using var pending = new ChatSubmissionState();
        var image = new CliImageAttachment("fixture.png", "image/png", "aW1hZ2U=", 1, 1, 5);
        pending.Add(image);
        using var commands = new AgentCommands(ChatTestSession.Value, http, TextWriter.Null, TextWriter.Null)
        {
            ResolveSubmissionHost = _ => Task.FromResult(Replacement),
        };
        var writer = new TgTranscriptWriter(app, window.Transcript);
        var path = Path.GetTempPath();
        var bootstrap = new AgentCommands.ChatBootstrapContext(pending, writer, path, path, SessionId,
            "OpenAI", "model-a", "", null, ChatPresentationMode.Native, true, writer, window, app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var submitted = false;
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

            if (!host.DeliveredReplacementEvents || window.Hints.IsRunning) return true;
            window.FocusComposer();
            window.Composer.Input.Text = "/exit";
            app.InjectKey(Key.Enter);
            return false;
        });

        Assert.Equal(0, await commands.RunPreparedChatAsync(new ChatCommand(path, SessionId, "standard", false), bootstrap, deadline.Token));
        Assert.True(host.DeliveredReplacementEvents);
        Assert.Equal(1, host.Created);
        Assert.Equal(SessionId, host.Submission.GetProperty("session_id").GetString());
        Assert.Equal("describe the attachment", host.Submission.GetProperty("prompt").GetString());
        Assert.Equal("model-a", host.Submission.GetProperty("provider_selection").GetProperty("model").GetString());
        Assert.Equal(image.ToDataUrl(), Assert.Single(host.Submission.GetProperty("image_attachments").EnumerateArray()).GetString());
    }

    [Theory]
    [InlineData("running")]
    [InlineData("waiting_user")]
    public async Task A_retained_run_prevents_replacement_on_a_failed_submission_read(string retainedStatus)
    {
        using var host = new Host { RetainedStatus = retainedStatus };
        using var http = new HttpClient(host);
        using var errors = new StringWriter();
        using var input = new ScriptedInput("new prompt\n/exit\n", line =>
        {
            if (line == 1) host.Disconnected = true;
        });
        var recoveryAttempts = 0;
        using var commands = new AgentCommands(ChatTestSession.Value, http, TextWriter.Null, errors, input)
        {
            ResolveSubmissionHost = _ =>
            {
                recoveryAttempts++;
                return Task.FromResult(Replacement);
            },
        };

        Assert.Equal(0, await commands.ChatAsync(new ChatCommand(Path.GetTempPath(), SessionId, "standard", Json: true), default));

        Assert.Equal(0, recoveryAttempts);
        Assert.Equal(0, host.Created);
        Assert.Contains("submission_settings_unavailable", errors.ToString());
        Assert.Contains(host.AfterDisconnect, request => request == (1, HttpMethod.Get, "/settings"));
    }

    private sealed class ScriptedInput(string lines, Action<int> beforeLine) : StringReader(lines)
    {
        private int _line;

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            beforeLine(++_line);
            return base.ReadLineAsync(cancellationToken);
        }
    }

    private sealed class Host : HttpMessageHandler
    {
        public bool Disconnected { get; set; }
        public bool RejectSettings { get; init; }
        public string ReplacementModel { get; init; } = "model-a";
        public bool ReplacementUnavailable { get; init; }
        public string? RetainedStatus { get; init; }
        public int Created { get; private set; }
        public JsonElement Submission { get; private set; }
        public volatile bool DeliveredReplacementEvents;
        public List<long> Cursors { get; } = [];
        public List<(int Port, HttpMethod Method, string Path)> AfterDisconnect { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath is "/agent/v1/runs" or "/agent/v1/runs/selected")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Submission = body.RootElement.Clone();
            }

            return Handle(request);
        }

        private HttpResponseMessage Handle(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            if (Disconnected)
            {
                AfterDisconnect.Add((uri.Port, request.Method, path));
                if (uri.Port == 1)
                {
                    if (RejectSettings) return Json(new { status = "error" }, HttpStatusCode.Forbidden);
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "Synthetic idle shutdown.");
                }

                Assert.Equal(Replacement.BearerToken, request.Headers.Authorization?.Parameter);
                if (ReplacementUnavailable)
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "Synthetic replacement unavailable.");
            }

            if (path == "/settings")
                return Json(new { status = "ok", backend = "OpenAI", openai_model = uri.Port == 1 ? "model-a" : ReplacementModel });
            if (path == $"/agent/v1/sessions/{SessionId}/runs" && RetainedStatus is not null)
                return Json(new { items = new[] { new { run_id = RunId } } });
            if (path == "/agent/v1/policies" && request.Method == HttpMethod.Post)
                return Json(new { session_id = SessionId, preset = "standard", version = 1 });
            if (request.Method == HttpMethod.Post && path is "/agent/v1/runs" or "/agent/v1/runs/selected")
            {
                Created++;
                return Json(Run(false), HttpStatusCode.Created);
            }

            if (path == $"/agent/v1/runs/{RunId}")
                return Json(Run(Disconnected));
            if (path == $"/agent/v1/runs/{RunId}/events")
            {
                if (uri.Port == Replacement.BaseUri.Port) DeliveredReplacementEvents = true;
                Cursors.Add(long.Parse(uri.Query.Split('&')[0]["?after_sequence=".Length..]));
                const int sequence = 1;
                var frame = JsonSerializer.Serialize(new
                {
                    protocol_version = 1, event_id = "evt_" + new string('b', 32),
                    run_id = RunId, session_id = SessionId, sequence,
                    event_type = "run.state_changed", occurred_at = "2026-11-06T00:00:00Z",
                    status = "completed",
                    safe_summary = "finished",
                });
                var wire = $"id: {sequence}\ndata: {frame}\n\n";
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(wire, Encoding.UTF8, "text/event-stream"),
                };
            }

            return Json(new { status = "ok", items = Array.Empty<object>(), models = Array.Empty<object>() });
        }

        private object Run(bool completed) => new
        {
            run_id = RunId, session_id = SessionId, workspace_id = "ws_" + new string('c', 32),
            status = completed ? "completed" : RetainedStatus ?? "running", preset = "standard", last_sequence = Cursors.Count > 0 ? 2 : 0,
            policy_snapshot_version = 1, policy_snapshot_sha256 = new string('d', 64), version = 1,
            created_at = "2026-11-06T00:00:00Z", updated_at = "2026-11-06T00:00:00Z",
            terminal_at = completed ? "2026-11-06T00:00:00Z" : null,
        };

        private static HttpResponseMessage Json(object body, HttpStatusCode code = HttpStatusCode.OK) =>
            new(code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
