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
    private sealed class Host(bool catalogUnavailable) : HttpMessageHandler
    {
        internal List<string> Posts = [];
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get) { Posts.Add(request.RequestUri!.AbsolutePath); throw new Exception("Cancellation must not mutate Host state"); }
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/models-dev") return Task.FromResult(catalogUnavailable
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }
                : Json(new { status = "ok", providers = new[] { new { id = "openrouter", name = "Synthetic OpenRouter" } } }));
            if (catalogUnavailable && path == "/models")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
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
    [InlineData("/model", true)]
    [InlineData("/backend")]
    [InlineData("/reasoning")]
    [InlineData("/provider", false)]
    [InlineData("/provider", true)]
    [InlineData("/preset")]
    [InlineData("/status")]
    [InlineData("/sessions")]
    [InlineData("/help")]
    [InlineData("/status overview")]
    [InlineData("/help", false, true)]
    public async Task Escape_closes_slash_panels_and_restores_composer_focus(
        string command, bool catalogUnavailable = false, bool inChat = false)
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var page = TgChatWindow.Build(); using var host = new Host(catalogUnavailable); using var http = new HttpClient(host);
        using var errors = new StringWriter();
        using var commands = new AgentCommands(ChatApprovalRulesWorkflowTests.Session, http, TextWriter.Null, errors);
        var writer = new TgTranscriptWriter(app, page.Transcript); var path = Path.GetTempPath();
        var bootstrap = new AgentCommands.ChatBootstrapContext(new ChatSubmissionState(), writer, path, path, string.Empty,
            "OpenAI", "synthetic-model-000", "default", null, ChatPresentationMode.Native, true, writer, page, app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        Exception? failure = null; var stage = 0; var beforeCancel = 0;
        double? pickerOpenedAt = null;
        var typedProviderFilter = false;
        var report = command is "/help" or "/status overview";
        app.AddTimeout(TimeSpan.FromMilliseconds(120), () =>
        {
            try
            {
                if (app.TopRunnableView is not TgSessionHostWindow) return true;
                if (stage == 0)
                {
                    if (inChat) page.Transcript.Append("Existing conversation", TranscriptStyle.Prose);
                    page.FocusComposer(); page.Composer.Input.Text = command; app.InjectKey(Key.Enter); stage++;
                }
                else if (stage == 1 && (report ? page.IsDocumentPanelOpen : page.IsModalPanelOpen))
                {
                    if (command is "/provider" or "/backend")
                    {
                        Assert.True(page.Landing.Visible, "Opening the provider picker must leave the welcome screen visible.");
                        Assert.False(page.Transcript.HasContent);
                        if (!typedProviderFilter)
                        {
                            app.InjectKey(Key.O);
                            typedProviderFilter = true;
                            return true;
                        }
                        var screen = string.Join('\n', TgSnapshot.Capture(app));
                        Assert.Contains("Search o", screen, StringComparison.OrdinalIgnoreCase);
                        Assert.Contains(catalogUnavailable ? "models.dev list unavailable" : "providers from models.dev", screen);
                    }
                    if (command == "/model")
                    {
                        Assert.True(page.Landing.Visible);
                        if (pickerOpenedAt is null)
                        {
                            pickerOpenedAt = page.Landing.AnimationTime;
                            return true;
                        }
                        Assert.True(page.Landing.AnimationTime > pickerOpenedAt.Value,
                            "The welcome galaxy should advance while the model picker owns focus.");
                        if (catalogUnavailable)
                            Assert.Contains("catalog unavailable", string.Join('\n', TgSnapshot.Capture(app)));
                    }
                    beforeCancel = page.Transcript.RetainedRowCount; app.InjectKey(Key.Esc); stage++;
                }
                else if (stage == 2 && !page.IsModalPanelOpen && !page.IsDocumentPanelOpen && page.Hints.ActiveState == TgHintsState.Idle)
                {
                    if (inChat)
                    {
                        Assert.True(page.Transcript.RetainedRowCount > beforeCancel);
                    }
                    else
                    {
                        Assert.Equal(beforeCancel, page.Transcript.RetainedRowCount);
                        if (report || command is "/provider" or "/backend" or "/model") Assert.True(page.Landing.Visible);
                    }
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
