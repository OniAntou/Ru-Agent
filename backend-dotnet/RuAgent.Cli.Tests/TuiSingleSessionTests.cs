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

public sealed class TuiSingleSessionTests
{
    [Theory]
    [InlineData("/new")]
    [InlineData("/sessions id:session_2")]
    public async Task Session_navigation_is_refused_while_the_single_worker_streams(string command)
    {
        using var host = new SingleHost();
        var stage = 0;
        await Run(host, (app, page) =>
        {
            switch (stage)
            {
                case 0: Send(app, page, "first prompt"); stage++; break;
                case 1 when host.Streams.Count == 1:
                    Send(app, page, command); stage++; break;
                case 2:
                    Assert.Contains("finish queued prompts", page.Hints.FeedbackMessage);
                    Assert.Equal(1, host.CreatedSessions); Assert.Single(host.Prompts);
                    Assert.Same(page, app.TopRunnableView);
                    app.InjectKey(Key.F6); app.InjectKey(Key.N.WithAlt);
                    Assert.Same(page, app.TopRunnableView); Assert.Equal(1, host.CreatedSessions);
                    Send(app, page, "/exit"); stage++; return false;
            }
            return true;
        });
        Assert.Equal(3, stage); Assert.Single(host.Cancels);
    }

    [Fact]
    public async Task New_replaces_the_settled_session_in_the_same_window()
    {
        using var host = new SingleHost(); var stage = 0;
        await Run(host, (app, page) =>
        {
            switch (stage)
            {
                case 0: Send(app, page, "first prompt"); stage++; break;
                case 1 when host.Streams.TryGetValue("session_1", out var run): host.Complete(run); stage++; break;
                case 2 when !page.Hints.IsRunning:
                    page.Header.UpdateUsage(42000, .7, 1.2m);
                    Send(app, page, "/new"); stage++; break;
                case 3 when host.CreatedSessions == 2 && !page.Hints.IsRunning:
                    Assert.Same(page, app.TopRunnableView); Assert.True(page.Landing.Visible);
                    Assert.Equal(0, page.Header.ContextTokens); Assert.Null(page.Header.SessionCost);
                    app.InjectKey(Key.CursorUp); Assert.Equal(string.Empty, page.Composer.Input.Text);
                    Send(app, page, "second prompt"); stage++; break;
                case 4 when host.Streams.ContainsKey("session_2"):
                    Assert.Equal(2, host.CreatedSessions); Assert.Empty(host.Cancels);
                    Assert.Equal("second prompt", host.Prompts["session_2"]);
                    Send(app, page, "/exit"); stage++; return false;
            }
            return true;
        });
        Assert.Equal(5, stage); Assert.Single(host.Cancels); Assert.True(host.Cancels.ContainsKey("session_2"));
    }

    [Fact]
    public async Task Continue_without_a_retained_run_is_an_ordinary_model_prompt()
    {
        using var host = new SingleHost(); var stage = 0;
        await Run(host, (app, page) =>
        {
            if (stage == 0) { Send(app, page, "Continue"); stage++; return true; }
            if (!host.Streams.ContainsKey("session_1")) return true;
            Assert.Equal("Continue", host.Prompts["session_1"]); Assert.Equal(1, host.CreatedSessions);
            Send(app, page, "/exit"); stage++; return false;
        });
        Assert.Equal(2, stage);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Exit_cancels_owned_work_and_detaches_adopted_work(bool adopted)
    {
        using var host = new SingleHost(); if (adopted) host.SeedExternalRun();
        var stage = 0;
        await Run(host, (app, page) =>
        {
            if (stage == 0) { if (!adopted) Send(app, page, "owned prompt"); stage++; return true; }
            if (host.Streams.Count == 0) return true;
            Send(app, page, "/exit"); stage++; return false;
        }, adopted ? "session_1" : "");
        Assert.Equal(2, stage);
        Assert.Equal(adopted ? 0 : 1, host.Cancels.Count);
    }

    [Fact]
    public async Task Exit_cancels_a_pending_session_creation_without_starting_a_worker()
    {
        using var host = new SingleHost { BlockSessionCreation = true }; var stage = 0;
        await Run(host, (app, page) =>
        {
            if (stage == 0) { Send(app, page, "/new"); stage++; return true; }
            if (host.CreatedSessions == 0) return true;
            Send(app, page, "/exit"); stage++; return false;
        });
        Assert.Equal(2, stage); Assert.True(host.CreateCancelled); Assert.Empty(host.Streams); Assert.Empty(host.Cancels);
    }

    [Fact]
    public async Task Admitted_model_stays_pinned_when_settings_change_during_policy_setup()
    {
        using var host = new SingleHost { DelayFirstPolicy = true }; var stage = 0;
        await Run(host, (app, page) =>
        {
            if (stage == 0) { Send(app, page, "pinned model"); stage++; return true; }
            if (stage == 1 && host.FirstPolicyEntered.Task.IsCompleted)
            {
                host._model = "gpt-4.1"; host.AllowFirstPolicy.TrySetResult(); stage++; return true;
            }
            if (stage != 2 || host.Streams.Count == 0) return true;
            Assert.Equal("gpt-4o", host.AdmittedModels["session_1"]);
            Send(app, page, "/exit"); stage++; return false;
        });
        Assert.Equal(3, stage);
    }

    [Theory]
    [InlineData("plan")] [InlineData("standard")] [InlineData("full_access")]
    public async Task Opening_saved_history_keeps_its_workspace_and_permission(string preset)
    {
        using var host = new SingleHost { RecordedWorkspace = Environment.CurrentDirectory, SavedPreset = preset };
        var stage = 0;
        await Run(host, (app, page) =>
        {
            if (stage == 0)
            {
                page.Header.UpdateUsage(42000, .7, 1.2m);
                Send(app, page, "/sessions id:session_2"); stage++; return true;
            }
            if (stage != 1) return true;
            if (page.Hints.IsRunning || page.Hints.FeedbackMessage is null) return true;
            Assert.Equal(preset, page.Composer.Preset); Assert.True(page.Landing.Visible);
            Assert.Equal(0, page.Header.ContextTokens); Assert.Null(page.Header.SessionCost);
            Assert.Same(page, app.TopRunnableView); Assert.Equal(0, host.CreatedSessions);
            Send(app, page, "work in saved workspace"); stage++;
            return true;
        }, finalTick: (app, page) =>
        {
            if (stage != 2 || !host.Streams.ContainsKey("session_2")) return null;
            Assert.Equal(Path.GetFullPath(Environment.CurrentDirectory), host.Workspaces["session_2"]);
            Send(app, page, "/exit"); stage++; return false;
        });
        Assert.Equal(3, stage);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Failed_saved_session_open_keeps_the_current_session_and_permission(bool missingFolder)
    {
        using var host = new SingleHost {
            RecordedWorkspace = missingFolder ? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) : Environment.CurrentDirectory,
            FailPresetRead = !missingFolder,
            SavedPreset = "full_access"
        };
        var stage = 0;
        await Run(host, (app, page) =>
        {
            if (stage == 0) { Send(app, page, "/sessions id:session_2"); stage++; return true; }
            if (stage != 1 || page.Hints.IsRunning || page.Hints.FeedbackMessage is null) return true;
            Assert.Equal("standard", page.Composer.Preset); Assert.True(page.Landing.Visible);
            Assert.Equal(0, host.CreatedSessions);
            Send(app, page, "stay in original draft"); stage++; return true;
        }, finalTick: (app, page) =>
        {
            if (stage != 2 || !host.Streams.ContainsKey("session_1")) return null;
            Assert.False(host.Prompts.ContainsKey("session_2"));
            Assert.Equal(Path.GetTempPath(), host.Workspaces["session_1"]);
            Send(app, page, "/exit"); stage++; return false;
        });
        Assert.Equal(3, stage);
    }

    private static void Send(IApplication app, TgChatWindow page, string text)
    { page.FocusComposer(); page.Composer.Input.Text = text; app.InjectKey(Key.Enter); }

    private static async Task Run(SingleHost host, Func<IApplication, TgChatWindow, bool> tick, string session = "", Func<IApplication, TgChatWindow, bool?>? finalTick = null)
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var page = TgChatWindow.Build(); using var http = new HttpClient(host);
        using var commands = new AgentCommands(ChatRewindWorkflowTests.Host.Session, http, TextWriter.Null, TextWriter.Null);
        using var submissions = new ChatSubmissionState();
        var writer = new TgTranscriptWriter(app, page.Transcript); var path = Path.GetTempPath();
        var bootstrap = new AgentCommands.ChatBootstrapContext(submissions, writer, path, path, session,
            "OpenAI", "gpt-4o", "high", null, ChatPresentationMode.Native, true, writer, page, app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); Exception? failure = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            try { return app.TopRunnableView is not TgChatWindow || (finalTick?.Invoke(app, page) ?? tick(app, page)); }
            catch (Exception error) { failure = error; app.RequestStop(); return false; }
        });
        Assert.Equal(0, await commands.RunPreparedChatAsync(new ChatCommand(path, session.Length == 0 ? null : session, "standard", false), bootstrap, deadline.Token));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class SingleHost : HttpMessageHandler
    {
        internal int CreatedSessions;
        internal bool BlockSessionCreation;
        internal bool CreateCancelled;
        internal bool DelayFirstPolicy;
        internal readonly TaskCompletionSource FirstPolicyEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource AllowFirstPolicy = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrentDictionary<string, string> AdmittedModels = new();
        internal readonly ConcurrentDictionary<string, string> Workspaces = new();
        private string? _externalRun;
        internal readonly ConcurrentDictionary<string, string> Streams = new();
        internal readonly ConcurrentDictionary<string, string> Prompts = new();
        internal readonly ConcurrentDictionary<string, string> Cancels = new();
        private readonly ConcurrentDictionary<string, string> _runSessions = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _release = new();
        private readonly ConcurrentDictionary<string, bool> _completed = new();
        internal string _model = "gpt-4o";
        internal string? RecordedWorkspace;
        internal string? SavedPreset;
        internal bool FailPresetRead;
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
            if (path == "/sessions") return Json(new { status = "ok", sessions = new[] { new { id = "session_2", title = "Saved session", workspace_path = RecordedWorkspace } } });
            if (path == "/settings")
            {
                if (request.Method != HttpMethod.Get)
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    if (body.RootElement.TryGetProperty("openai_model", out var model)) _model = model.GetString()!;
                }
                return Json(new { status = "ok", backend = "OpenAI", openai_model = _model });
            }
            if (FailPresetRead && path.Contains("/session_2/preset", StringComparison.Ordinal)) return Json(new { error = "unavailable" }, HttpStatusCode.InternalServerError);
            if (path.EndsWith("/preset")) return Json(new { session_id = path.Split('/')[4], preset = path.Contains("/session_2/preset", StringComparison.Ordinal) ? SavedPreset : null, version = 0 });
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
                return Json(new { session_id = session, preset = body.RootElement.GetProperty("preset").GetString(), version = 1 });
            }
            if (path is "/agent/v1/runs" or "/agent/v1/runs/selected")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var session = body.RootElement.GetProperty("session_id").GetString()!;
                Workspaces[session] = body.RootElement.GetProperty("workspace_path").GetString()!;
                AdmittedModels[session] = path.EndsWith("/selected", StringComparison.Ordinal)
                    ? body.RootElement.GetProperty("provider_selection").GetProperty("model").GetString()! : _model;
                var run = "run_" + Guid.NewGuid().ToString("N");
                _runSessions[run] = session;
                _release[run] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Prompts[session] = body.RootElement.GetProperty("prompt").GetString()!;
                return Json(new { run_id = run, session_id = session, workspace_id = "ws_" + new string('a', 32),
                    status = "running",
                    preset = session == "session_2" ? SavedPreset ?? "standard" : "standard", created_at = "2026-10-30T00:00:00Z", updated_at = "2026-10-30T00:00:00Z" }, HttpStatusCode.Created);
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
                    status = done ? "completed" : "running", preset = owner == "session_2" ? SavedPreset ?? "standard" : "standard", version = 1, last_sequence = done ? 1 : 0,
                    policy_snapshot_version = 1, policy_snapshot_sha256 = new string('a', 64),
                    created_at = "2026-10-30T00:00:00Z", updated_at = "2026-10-30T00:00:01Z",
                    terminal_at = done ? "2026-10-30T00:00:01Z" : null });
            }
            return Json(new { status = "ok", items = Array.Empty<object>(), providers = Array.Empty<object>(), models = Array.Empty<object>() });
        }
    }
}
