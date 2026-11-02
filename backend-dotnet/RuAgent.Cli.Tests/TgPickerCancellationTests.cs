using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Cli.TerminalGui;
using RuAgent.Client;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgPickerCancellationTests
{
    private sealed class Host : HttpMessageHandler
    {
        internal List<string> Posts = [];
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get) { Posts.Add(request.RequestUri!.AbsolutePath); throw new Exception("Cancellation must not mutate Host state"); }
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/settings/models-dev") return Task.FromResult(Json(new { status = "ok",
                providers = new[] { new { id = "openrouter", name = "Synthetic OpenRouter" } } }));
            if (path is "/models" or "/settings/models") return Task.FromResult(Json(new { status = "ok", backend = "OpenAI", identity = "synthetic-model-identity",
                selected_model = "synthetic-model-000", selected_effort = "default", models = Array.Empty<object>(),
                catalog = Enumerable.Range(0, 400).Select(index => new { id = $"synthetic-model-{index:000}", supported = true,
                    reasoning = new { state = "levels", options = new[] { "low", "high" } } }) }));
            if (path == "/sessions") return Task.FromResult(Json(new { status = "ok",
                sessions = new[] { new { id = "saved-synthetic-session", title = "Saved synthetic session" } } }));
            if (path == "/settings") return Task.FromResult(Json(new { status = "ok", backend = "OpenAI",
                openai_model = "synthetic-model-000", anthropic_model = "synthetic-anthropic-model", has_openai_api_key = true }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        }
    }

    [Theory]
    [InlineData("/model")]
    [InlineData("/backend")]
    [InlineData("/reasoning")]
    [InlineData("/provider")]
    [InlineData("/preset")]
    [InlineData("/status")]
    [InlineData("/sessions")]
    public async Task Escape_closes_picker_without_appending_options_or_losing_composer_focus(string command)
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var page = TgChatWindow.Build(); using var host = new Host(); using var http = new HttpClient(host);
        using var errors = new StringWriter();
        using var commands = new AgentCommands(ChatApprovalRulesWorkflowTests.Session, http, TextWriter.Null, errors);
        var writer = new TgTranscriptWriter(app, page.Transcript); var path = Path.GetTempPath();
        var bootstrap = new AgentCommands.ChatBootstrapContext(new ChatSubmissionState(), writer, path, path, string.Empty,
            "OpenAI", "synthetic-model-000", "default", null, ChatPresentationMode.Native, true, writer, page, app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        Exception? failure = null; var stage = 0; var beforeCancel = 0;
        app.AddTimeout(TimeSpan.FromMilliseconds(120), () =>
        {
            try
            {
                if (app.TopRunnableView is not TgSessionHostWindow) return true;
                if (stage == 0)
                { page.FocusComposer(); page.Composer.Input.Text = command; app.InjectKey(Key.Enter); stage++; }
                else if (stage == 1 && page.IsModalPanelOpen)
                { beforeCancel = page.Transcript.RetainedRowCount; app.InjectKey(Key.Esc); stage++; }
                else if (stage == 2 && !page.IsModalPanelOpen && page.Hints.ActiveState == TgHintsState.Idle)
                {
                    Assert.Equal(beforeCancel, page.Transcript.RetainedRowCount);
                    Assert.DoesNotContain(page.Transcript.Lines, row => row.Text.Contains("synthetic-model-399"));
                    Assert.Same(page.Composer.Input, page.MostFocused);
                    app.InjectKey(Key.X); Assert.Equal("x", page.Composer.Input.Text);
                    Assert.Empty(host.Posts);
                    page.Composer.Input.Text = "/exit"; app.InjectKey(Key.Enter); stage++; return false;
                }
            }
            catch (Exception error) { failure = error; app.RequestStop(); return false; }
            return !deadline.IsCancellationRequested;
        });
        var exit = await commands.RunPreparedChatAsync(new ChatCommand(path, null, "standard", false), bootstrap, deadline.Token);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(3, stage); Assert.Equal(0, exit); Assert.Empty(host.Posts);
        Assert.DoesNotContain("settings_error", errors.ToString());
    }
}
