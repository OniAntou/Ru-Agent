using System.Collections.Concurrent;
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

public sealed partial class TuiMultiSessionTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public async Task Two_real_chat_workers_keep_their_run_requests_alive_across_session_switches(bool changeModel, bool rejectB)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var pageA = TgChatWindow.Build();
        using var host = new ConcurrentHost { RejectSecondRun = rejectB };
        using var http = new HttpClient(host);
        using var commands = new AgentCommands(ChatRewindWorkflowTests.Host.Session, http, TextWriter.Null, TextWriter.Null);
        var writer = new TgTranscriptWriter(app, pageA.Transcript);
        var submissions = new ChatSubmissionState();
        var path = Path.GetTempPath();
        var bootstrap = new AgentCommands.ChatBootstrapContext(submissions, writer, path, path, string.Empty,
            "OpenAI", "gpt-4o", "high", null, ChatPresentationMode.Native, true, writer, pageA, app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Exception? failure = null;
        var stage = 0;
        TgChatWindow? pageB = null;
        string? firstRunA = null;
        var modelCommandSent = false;
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            try
            {
                if (app.TopRunnableView is not TgSessionHostWindow shell) return true;
                if (stage == 0)
                {
                    Assert.Equal(0, host.CreatedSessions);
                    pageA.FocusComposer();
                    pageA.Composer.Input.Text = "/";
                    Assert.Equal(0, host.CreatedSessions);
                    pageA.Composer.Input.Text = "work in A";
                    app.InjectKey(Key.Enter);
                    stage++;
                }
                else if (stage == 1 && host.Streams.ContainsKey("session_1"))
                {
                    firstRunA = host.Streams["session_1"];
                    pageA.Composer.Input.Text = "queued in A";
                    app.InjectKey(Key.Enter);
                    pageA.Composer.Input.Text = "/new";
                    app.InjectKey(Key.Enter);
                    stage++;
                }
                else if (stage == 2 && !ReferenceEquals(shell.ActivePage, pageA))
                {
                    pageB = shell.ActivePage!;
                    pageB.FocusComposer();
                    if (changeModel && !modelCommandSent)
                    {
                        pageB.Composer.Input.Text = "/model gpt-4.1";
                        app.InjectKey(Key.Enter); modelCommandSent = true; return true;
                    }
                    if (changeModel && (pageB.Composer.Model != "gpt-4.1" || pageB.Hints.IsRunning)) return true;
                    pageB.Composer.Input.Text = "work in B";
                    app.InjectKey(Key.Enter);
                    stage++;
                }
                else if (stage == 3 && host.Streams.Count == 2)
                {
                    Assert.Empty(host.Cancels);
                    Assert.False(pageA.Visible); Assert.True(pageB!.Visible);
                    pageB.Composer.Input.Text = "draft B";
                    app.InjectKey(Key.F6);
                    Assert.Same(pageA, shell.ActivePage);
                    pageA.Composer.Input.Text = "draft A";
                    app.InjectKey(Key.F6);
                    Assert.Same(pageB, shell.ActivePage);
                    Assert.Equal("draft B", pageB.Composer.Input.Text);
                    Assert.Empty(host.Cancels);
                    Assert.Equal("work in A", host.Prompts["session_1"]);
                    Assert.Equal("work in B", host.Prompts["session_2"]);
                    host.Complete(firstRunA!);
                    stage++;
                }
                else if (stage == 4 && (changeModel ? pageA.Hints.ActiveState == TgHintsState.Paused
                    : host.Streams.GetValueOrDefault("session_1") != firstRunA))
                {
                    Assert.Same(pageB, shell.ActivePage);
                    Assert.Equal("draft B", pageB!.Composer.Input.Text);
                    Assert.Equal(changeModel ? "work in A" : "queued in A", host.Prompts["session_1"]);
                    Assert.Empty(host.Cancels);
                    var visible = string.Join('\n', TgSnapshot.Capture(app));
                    Assert.DoesNotContain("queued in A", visible);
                    app.InjectKey(Key.F6);
                    Assert.Equal("draft A", pageA.Composer.Input.Text);
                    pageA.Composer.Input.Text = "/exit";
                    app.InjectKey(Key.Enter);
                    stage++;
                    return false;
                }
            }
            catch (Exception error) { failure = error; app.RequestStop(); return false; }
            return !deadline.IsCancellationRequested;
        });
        var exit = await commands.RunPreparedChatAsync(new ChatCommand(path, null, "standard", false), bootstrap, deadline.Token);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (rejectB)
        {
            Assert.Equal(3, stage); Assert.Equal(1, exit);
            Assert.Contains(firstRunA!, host.Cancels.Values);
            return;
        }
        Assert.Equal(5, stage);
        Assert.Equal(0, exit);
        Assert.Equal(2, host.CreatedSessions);
        Assert.Equal(changeModel ? 1 : 2, host.Cancels.Count);
        Assert.DoesNotContain(firstRunA!, host.Cancels.Values);
    }

    [Fact]
    public async Task Idle_tabs_adopt_new_model_defaults_and_reopening_does_not_duplicate_a_tab()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var pageA = TgChatWindow.Build();
        using var host = new ConcurrentHost(); using var http = new HttpClient(host);
        using var commands = new AgentCommands(ChatRewindWorkflowTests.Host.Session, http, TextWriter.Null, TextWriter.Null);
        var writer = new TgTranscriptWriter(app, pageA.Transcript);
        var path = Path.GetTempPath();
        var bootstrap = new AgentCommands.ChatBootstrapContext(new ChatSubmissionState(), writer, path, path, string.Empty,
            "OpenAI", "gpt-4o", "high", null, ChatPresentationMode.Native, true, writer, pageA, app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Exception? failure = null; var stage = 0; TgChatWindow? pageB = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            try
            {
                if (app.TopRunnableView is not TgSessionHostWindow shell) return true;
                switch (stage)
                {
                    case 0:
                        pageA.FocusComposer(); pageA.Composer.Input.Text = "draft A";
                        app.InjectKey(Key.N.WithAlt); stage++; break;
                    case 1 when !ReferenceEquals(shell.ActivePage, pageA):
                        pageB = shell.ActivePage!; pageB.FocusComposer();
                        pageB.Composer.Input.Text = "/model gpt-4.1"; app.InjectKey(Key.Enter); stage++; break;
                    case 2 when pageB!.Composer.Model == "gpt-4.1" && !pageB.Hints.IsRunning:
                        app.InjectKey(Key.F6.WithShift);
                        Assert.Same(pageA, shell.ActivePage);
                        Assert.Equal("draft A", pageA.Composer.Input.Text);
                        Assert.Equal("gpt-4.1", pageA.Composer.Model);
                        pageA.Composer.Input.Text = "fresh model in A"; app.InjectKey(Key.Enter); stage++; break;
                    case 3 when host.Streams.ContainsKey("session_2"):
                        Assert.Equal("fresh model in A", host.Prompts["session_2"]);
                        host.Complete(host.Streams["session_2"]); stage++; break;
                    case 4 when !pageA.Hints.IsRunning:
                        pageA.Composer.Input.Text = "/sessions close"; app.InjectKey(Key.Enter);
                        Assert.Same(pageB, shell.ActivePage);
                        pageB!.Composer.Input.Text = "/sessions id:session_1"; app.InjectKey(Key.Enter); stage++; break;
                    case 5:
                        Assert.Same(pageB, shell.ActivePage); Assert.Equal(2, host.CreatedSessions);
                        pageB!.Composer.Input.Text = "/exit"; app.InjectKey(Key.Enter); stage++; return false;
                }
            }
            catch (Exception error) { failure = error; app.RequestStop(); return false; }
            return !deadline.IsCancellationRequested;
        });
        Assert.Equal(0, await commands.RunPreparedChatAsync(new ChatCommand(path, null, "standard", false), bootstrap, deadline.Token));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(6, stage); Assert.Empty(host.Cancels);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Adopted_runs_detach_on_exit_but_explicit_escape_cancels_them(bool explicitEscape)
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var page = TgChatWindow.Build();
        using var host = new ConcurrentHost(); host.SeedExternalRun();
        using var http = new HttpClient(host);
        using var commands = new AgentCommands(ChatRewindWorkflowTests.Host.Session, http, TextWriter.Null, TextWriter.Null);
        var writer = new TgTranscriptWriter(app, page.Transcript); var path = Path.GetTempPath();
        var bootstrap = new AgentCommands.ChatBootstrapContext(new ChatSubmissionState(), writer, path, path, "session_1",
            "OpenAI", "gpt-4o", "high", null, ChatPresentationMode.Native, true, writer, page, app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stage = 0;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            if (host.Streams.Count == 0) return true;
            if (explicitEscape && stage == 0) { app.InjectKey(Key.Esc); stage++; return true; }
            if (explicitEscape && host.Cancels.Count == 0) return true;
            page.Composer.Input.Text = "/exit"; app.InjectKey(Key.Enter); stage++; return false;
        });
        Assert.Equal(0, await commands.RunPreparedChatAsync(new ChatCommand(path, "session_1", "standard", false), bootstrap, deadline.Token));
        Assert.Equal(explicitEscape ? 2 : 1, stage);
        Assert.Equal(explicitEscape ? 1 : 0, host.Cancels.Count);
        Assert.Equal(0, host.CreatedSessions);
    }

    [Fact]
    public async Task Exit_cancels_an_outstanding_session_create_without_repeating_the_post()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var page = TgChatWindow.Build();
        using var host = new ConcurrentHost { BlockSessionCreation = true }; using var http = new HttpClient(host);
        using var commands = new AgentCommands(ChatRewindWorkflowTests.Host.Session, http, TextWriter.Null, TextWriter.Null);
        var writer = new TgTranscriptWriter(app, page.Transcript); var path = Path.GetTempPath();
        var bootstrap = new AgentCommands.ChatBootstrapContext(new ChatSubmissionState(), writer, path, path, string.Empty,
            "OpenAI", "gpt-4o", "high", null, ChatPresentationMode.Native, true, writer, page, app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stage = 0;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            if (app.TopRunnableView is not TgSessionHostWindow) return true;
            page.FocusComposer();
            if (stage == 0) { page.Composer.Input.Text = "/new"; app.InjectKey(Key.Enter); stage++; return true; }
            if (host.CreatedSessions == 0) return true;
            page.Composer.Input.Text = "/exit"; app.InjectKey(Key.Enter); stage++; return false;
        });
        Assert.Equal(0, await commands.RunPreparedChatAsync(new ChatCommand(path, null, "standard", false), bootstrap, deadline.Token));
        Assert.Equal(2, stage); Assert.Equal(1, host.CreatedSessions); Assert.True(host.CreateCancelled);
        Assert.Empty(host.Streams); Assert.Empty(host.Cancels);
    }

    private sealed class ConcurrentHost : HttpMessageHandler
    {
        internal int CreatedSessions;
        internal bool BlockSessionCreation;
        internal bool CreateCancelled;
        internal bool RejectSecondRun;
        internal bool DelayFirstPolicy;
        internal readonly TaskCompletionSource FirstPolicyEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource AllowFirstPolicy = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrentDictionary<string, string> AdmittedModels = new();
        private string? _externalRun;
        internal readonly ConcurrentDictionary<string, string> Streams = new();
        internal readonly ConcurrentDictionary<string, string> Prompts = new();
        internal readonly ConcurrentDictionary<string, string> Cancels = new();
        private readonly ConcurrentDictionary<string, string> _runSessions = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _release = new();
        private readonly ConcurrentDictionary<string, bool> _completed = new();
        private string _model = "gpt-4o";
        internal void Complete(string run) { _completed[run] = true; _release[run].TrySetResult(); }
        internal void SeedExternalRun()
        {
            _externalRun = "run_" + Guid.NewGuid().ToString("N");
            _runSessions[_externalRun] = "session_1";
            _release[_externalRun] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/settings")
            {
                if (request.Method != HttpMethod.Get)
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    if (body.RootElement.TryGetProperty("openai_model", out var model)) _model = model.GetString()!;
                }
                return Json(new { status = "ok", backend = "OpenAI", openai_model = _model });
            }
            if (path.EndsWith("/preset")) return Json(new { session_id = path.Split('/')[4], preset = (string?)null, version = 0 });
            if (path == "/sessions/new")
            {
                var id = "session_" + Interlocked.Increment(ref CreatedSessions);
                if (BlockSessionCreation)
                {
                    try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct); }
                    catch (OperationCanceledException) { CreateCancelled = true; throw; }
                }
                return Json(new { status = "ok", session_id = id });
            }
            if (path.StartsWith("/agent/v1/sessions/", StringComparison.Ordinal) && path.EndsWith("/runs", StringComparison.Ordinal))
                return Json(new { items = _externalRun is null ? Array.Empty<object>() : [new { run_id = _externalRun }] });
            if (path == "/agent/v1/policies")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var session = body.RootElement.GetProperty("session_id").GetString();
                if (DelayFirstPolicy && session == "session_1")
                {
                    FirstPolicyEntered.TrySetResult(); await AllowFirstPolicy.Task.WaitAsync(ct);
                }
                return Json(new { session_id = session, preset = "standard", version = 1 });
            }
            if (path is "/agent/v1/runs" or "/agent/v1/runs/selected")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var session = body.RootElement.GetProperty("session_id").GetString()!;
                AdmittedModels[session] = path.EndsWith("/selected", StringComparison.Ordinal)
                    ? body.RootElement.GetProperty("provider_selection").GetProperty("model").GetString()! : _model;
                var run = "run_" + Guid.NewGuid().ToString("N");
                _runSessions[run] = session;
                _release[run] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Prompts[session] = body.RootElement.GetProperty("prompt").GetString()!;
                return Json(new { run_id = run, session_id = session, workspace_id = "ws_" + new string('a', 32),
                    status = RejectSecondRun && session == "session_2" ? "not_a_state" : "running",
                    preset = "standard", created_at = "2026-10-30T00:00:00Z", updated_at = "2026-10-30T00:00:00Z" }, HttpStatusCode.Created);
            }
            if (path.EndsWith("/events"))
            {
                var run = path.Split('/')[4];
                Streams[_runSessions[run]] = run;
                await _release[run].Task.WaitAsync(ct);
                var frame = JsonSerializer.Serialize(new { protocol_version = 1, event_id = "evt_" + Guid.NewGuid().ToString("N"),
                    run_id = run, session_id = _runSessions[run], sequence = 1, event_type = "run.state_changed",
                    occurred_at = "2026-10-30T00:00:01Z", status = "completed", safe_summary = "done" });
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("id: 1\ndata: " + frame + "\n\n", Encoding.UTF8, "text/event-stream") };
            }
            if (path.EndsWith("/cancel"))
            {
                var run = path.Split('/')[4];
                Cancels[_runSessions[run]] = run;
                return Json(new { run_id = run, status = "cancelled", version = 2 });
            }
            if (path.EndsWith("/activate") || path.StartsWith("/sessions/session_"))
            {
                var session = path.Split('/')[2];
                return Json(new { status = "ok", session = new { id = session, title = session, messages = Array.Empty<object>() } });
            }
            if (_runSessions.TryGetValue(path.Split('/').Last(), out var owner))
            {
                var run = path.Split('/').Last();
                var done = _completed.ContainsKey(run);
                return Json(new { run_id = run, session_id = owner, workspace_id = "ws_" + new string('a', 32),
                    status = done ? "completed" : "running", preset = "standard", version = 1, last_sequence = done ? 1 : 0,
                    policy_snapshot_version = 1, policy_snapshot_sha256 = new string('a', 64),
                    created_at = "2026-10-30T00:00:00Z", updated_at = "2026-10-30T00:00:01Z",
                    terminal_at = done ? "2026-10-30T00:00:01Z" : null });
            }
            return Json(new { status = "ok", items = Array.Empty<object>(), providers = Array.Empty<object>(), models = Array.Empty<object>() });
        }
    }
}
