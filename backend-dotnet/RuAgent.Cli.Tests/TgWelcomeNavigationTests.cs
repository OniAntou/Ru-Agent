using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;
using Xunit.Abstractions;

namespace RuAgent.Cli.Tests;

public sealed class TgWelcomeNavigationTests(ITestOutputHelper output)
{
    private sealed class Host(string profile) : HttpMessageHandler
    {
        internal ConcurrentQueue<string> Mutations { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get)
            {
                Mutations.Enqueue(request.Method + " " + path);
                return Task.FromResult(path == "/settings" ? Json(new { status = "success" }) : Error(HttpStatusCode.Conflict));
            }
            return Task.FromResult(path switch
            {
                "/settings" => Json(new { status = "ok", backend = "OpenAI", openai_model = profile == "no-model" ? "" : "audit-model-1",
                    anthropic_model = profile == "one-model" || profile == "no-model" ? "" : "audit-claude", has_openai_api_key = true }),
                "/models-dev" => profile == "provider-error" ? Error(HttpStatusCode.ServiceUnavailable)
                    : Json(new { status = "ok", providers = new[] { new { id = "openrouter", name = "OpenRouter" } } }),
                "/settings/models-dev-keys" => Json(new { status = "ok", provider_ids = Array.Empty<string>() }),
                "/models" or "/settings/models" => Models(),
                "/sessions" => profile == "sessions-error" ? Error(HttpStatusCode.ServiceUnavailable)
                    : Json(new { status = "ok", sessions = profile == "sessions-empty" ? Array.Empty<object>()
                        : new object[] { new { id = "audit-session", title = "Audit session" } } }),
                _ => Error(HttpStatusCode.NotFound),
            });
        }

        private HttpResponseMessage Models()
        {
            if (profile is "models-error" or "one-model" or "no-model") return Error(HttpStatusCode.ServiceUnavailable);
            var reasoning = profile == "no-effort" ? new { state = "none", options = Array.Empty<string>() }
                : new { state = "levels", options = new[] { "low", "high" } };
            return Json(new { status = "ok", backend = "OpenAI", identity = "audit-model-identity", selected_model = "audit-model-1",
                selected_effort = "default", models = Array.Empty<object>(),
                catalog = new[] { new { id = "audit-model-1", supported = true, reasoning }, new { id = "audit-model-2", supported = true, reasoning } } });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Error(HttpStatusCode code) => new(code) { Content = new StringContent("{}") };
    }

    [Theory]
    [InlineData("/model", "normal", "picker")]
    [InlineData("/model", "models-error", "picker")]
    [InlineData("/model", "one-model", "document")]
    [InlineData("/model", "no-model", "document")]
    [InlineData("/model audit-model-2", "normal", "none", true)]
    [InlineData("/provider", "normal", "filter")]
    [InlineData("/provider", "provider-error", "filter")]
    [InlineData("/provider", "normal", "picker-key")]
    [InlineData("/provider openai", "normal", "picker")]
    [InlineData("/provider bad-provider", "normal", "none")]
    [InlineData("/provider openai", "normal", "confirm-key", true)]
    [InlineData("/backend", "provider-error", "filter")]
    [InlineData("/key openai", "normal", "picker")]
    [InlineData("/reasoning", "normal", "picker")]
    [InlineData("/reasoning", "no-effort", "none")]
    [InlineData("/reasoning", "models-error", "none")]
    [InlineData("/reasoning invalid-effort", "normal", "none")]
    [InlineData("/computer", "normal", "none")]
    [InlineData("/computer on", "normal", "none", true)]
    [InlineData("/computer invalid", "normal", "none")]
    [InlineData("/status", "normal", "picker")]
    [InlineData("/status overview", "normal", "document")]
    [InlineData("/status context", "normal", "document")]
    [InlineData("/status usage", "normal", "document")]
    [InlineData("/status invalid", "normal", "none")]
    [InlineData("/preset", "normal", "picker")]
    [InlineData("/preset plan", "normal", "none")]
    [InlineData("/preset invalid", "normal", "none")]
    [InlineData("/preset rules", "normal", "none")]
    [InlineData("/sessions", "normal", "filter")]
    [InlineData("/sessions", "sessions-empty", "none")]
    [InlineData("/sessions absent", "normal", "none")]
    [InlineData("/sessions", "sessions-error", "document")]
    [InlineData("/sessions close", "normal", "none")]
    [InlineData("/sessions id:bad!", "normal", "none")]
    [InlineData("/subagents", "normal", "none")]
    [InlineData("/tasks", "normal", "none")]
    [InlineData("/changes", "normal", "none")]
    [InlineData("/history", "normal", "none")]
    [InlineData("/history cancel", "normal", "none")]
    [InlineData("/rewind", "normal", "none")]
    [InlineData("/follow", "normal", "none")]
    [InlineData("/help", "normal", "document")]
    [InlineData("/update --invalid", "normal", "none")]
    [InlineData("/not-a-real-ru-command", "normal", "none")]
    [InlineData("/skills", "normal", "none")]
    public async Task Local_commands_keep_welcome_visible_and_only_confirmed_settings_write(string command, string profile, string interaction, bool savesSettings = false)
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var page = TgChatWindow.Build(); using var host = new Host(profile); using var http = new HttpClient(host);
        using var errors = new StringWriter(); using var commands = new AgentCommands(ChatApprovalRulesWorkflowTests.Session, http, TextWriter.Null, errors);
        using var submissions = new ChatSubmissionState();
        var writer = new TgTranscriptWriter(app, page.Transcript); var path = Path.GetTempPath();
        var bootstrap = new AgentCommands.ChatBootstrapContext(submissions, writer, path, path, string.Empty,
            "OpenAI", "audit-model-1", "default", null, ChatPresentationMode.Native, true, writer, page, app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var clock = Stopwatch.StartNew(); var waitUntil = TimeSpan.Zero;
        var step = 0; var panels = 0; var observedWelcome = new List<bool>(); Exception? failure = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            try
            {
                if (app.TopRunnableView is not TgChatWindow) return true;
                if (step == 0)
                {
                    Assert.True(page.Landing.Visible);
                    page.FocusComposer(); page.Composer.Input.Text = command; app.InjectKey(Key.Enter);
                    waitUntil = clock.Elapsed + TimeSpan.FromMilliseconds(600); step = 1;
                    return true;
                }

                if (step is 1 or 2 && (page.IsModalPanelOpen || page.IsDocumentPanelOpen))
                {
                    observedWelcome.Add(page.Landing.Visible);
                    panels++;
                    if (step == 1 && interaction == "filter")
                    {
                        foreach (var ch in "zzzzzz") app.InjectKey(new Key(ch));
                        step = 2;
                        return true;
                    }
                    if (step == 1 && interaction == "picker-key")
                    {
                        app.InjectKey(Key.Enter);
                        step = 2;
                        return true;
                    }
                    app.InjectKey(interaction == "confirm-key" ? Key.Enter : Key.Esc);
                    waitUntil = clock.Elapsed + TimeSpan.FromMilliseconds(300); step = 3;
                    return true;
                }

                if (clock.Elapsed < waitUntil || page.IsModalPanelOpen || page.IsDocumentPanelOpen || page.Hints.ActiveState != TgHintsState.Idle) return true;
                var expectedPanels = interaction == "none" ? 0 : interaction is "filter" or "picker-key" ? 2 : 1;
                Assert.Equal(expectedPanels, panels);
                var line = page.Transcript.Lines.Select(row => row.Text.Trim()).FirstOrDefault(row => row.Length > 0) ?? "";
                output.WriteLine("AUDIT|{0}|{1}|{2}|during={3}|after={4}|rows={5}|output={6}|stderr={7}|writes={8}",
                    command, profile, interaction, string.Join(',', observedWelcome), page.Landing.Visible, page.Transcript.RetainedRowCount,
                    line, errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "", string.Join(',', host.Mutations));
                Assert.Equal(savesSettings ? new[] { "POST /settings" } : Array.Empty<string>(), host.Mutations.ToArray());
                Assert.True(page.Landing.Visible, "Local command unexpectedly opened the chat transcript.");
                Assert.False(page.Transcript.HasContent);
                if (interaction == "none") Assert.False(string.IsNullOrWhiteSpace(page.Hints.FeedbackMessage));
                Assert.Same(page.Composer.Input, page.MostFocused);
                page.Composer.Input.Text = "/exit"; app.InjectKey(Key.Enter); step = 4; return false;
            }
            catch (Exception ex) { failure = ex; app.RequestStop(); return false; }
        });
        var exit = await commands.RunPreparedChatAsync(new ChatCommand(path, null, "standard", false), bootstrap, deadline.Token);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(4, step);
        Assert.Equal(0, exit);
    }
}
