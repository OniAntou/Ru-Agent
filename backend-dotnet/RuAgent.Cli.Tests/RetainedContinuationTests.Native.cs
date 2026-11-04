using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Cli.Attachments;
using RuAgent.Cli.TerminalGui;
using RuAgent.Protocol.Harness;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class RetainedContinuationTests
{
    [Fact]
    public async Task Continue_reopens_a_deferred_native_question_without_sending_the_draft_or_creating_a_run()
    {
        var run = ChatInputWorkflowTests.RunId;
        var session = ChatInputWorkflowTests.SessionId;
        var input = ChatInputWorkflowTests.InputId;
        var payload = "payload_" + new string('d', 32);
        var mutations = 0; var questions = 0;
        using var http = new HttpClient(new ChatInputWorkflowTests.Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            // Opening saved history activates it; recovery itself stays read-only.
            if (request.Method != HttpMethod.Get && path != $"/sessions/{session}/activate")
                Interlocked.Increment(ref mutations);
            string? body = path switch
            {
                "/settings" => JsonSerializer.Serialize(new { status = "ok", backend = "OpenAI", openai_model = "gpt-4o" }),
                var p when p == $"/sessions/{session}/activate" => JsonSerializer.Serialize(new { status = "ok", session = new { id = session, title = "Question", messages = Array.Empty<object>() } }),
                var p when p == $"/agent/v1/sessions/{session}/runs" => JsonSerializer.Serialize(new { items = new[] { new { run_id = run } } }),
                var p when p == $"/agent/v1/runs/{run}" => ChatInputWorkflowTests.Summary(),
                _ => null
            };
            if (body is not null) return Task.FromResult(ChatInputWorkflowTests.Json(body));
            if (path == $"/agent/v1/runs/{run}/events")
            {
                Assert.Contains("live=0", request.RequestUri.Query);
                var frame = JsonSerializer.Serialize(new HarnessSafeEventEnvelope(1, "evt_question", run, session, 5,
                    "input.requested", "2026-10-25T00:00:00Z", "waiting_input", "input_required", payload));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent($"id: 5\ndata: {frame}\n\n", Encoding.UTF8, "text/event-stream") });
            }
            if (path == $"/agent/v1/runs/{run}/payloads/{payload}")
            {
                Interlocked.Increment(ref questions);
                return Task.FromResult(ChatInputWorkflowTests.Json(JsonSerializer.Serialize(new {
                    run_id = run, payload_id = payload, kind = "userinputrequest", encoding = "json",
                    content = JsonSerializer.Serialize(new { input_id = input, question = "Which environment?", options = new[] { "staging" } })
                })));
            }
            return Task.FromResult(ChatInputWorkflowTests.Json("{}", HttpStatusCode.NotFound));
        }));
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var page = TgChatWindow.Build(); using var pending = new ChatSubmissionState();
        using var commands = new AgentCommands(ChatInputWorkflowTests.Session, http, TextWriter.Null, TextWriter.Null);
        var writer = new TgTranscriptWriter(app, page.Transcript); var path = Path.GetTempPath();
        var bootstrap = new AgentCommands.ChatBootstrapContext(pending, writer, path, path, session,
            "OpenAI", "gpt-4o", "high", null, ChatPresentationMode.Native, true, writer, page, app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var stage = 0; Exception? failure = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            try
            {
                if (app.TopRunnableView is not TgChatWindow) return true;
                if (stage == 0 && page.SubViews.OfType<TgQuestionPanel>().Any())
                {
                    app.InjectKey(Key.Esc); stage++; return true;
                }
                if (stage == 1 && !page.IsModalPanelOpen && page.Hints.ActiveState == TgHintsState.Paused)
                {
                    pending.Add(new CliTextAttachment("draft.md", "unsent content", 14));
                    page.FocusComposer(); page.Composer.Input.Text = "Continue";
                    app.InjectKey(Key.Enter); stage++; return true;
                }
                if (stage == 2 && page.SubViews.OfType<TgQuestionPanel>().Any())
                {
                    Assert.Equal(1, pending.Count);
                    app.InjectKey(Key.Esc); stage++; return true;
                }
                if (stage == 3 && !page.IsModalPanelOpen && page.Hints.ActiveState == TgHintsState.Paused)
                {
                    Assert.Equal(1, pending.Count);
                    page.FocusComposer(); page.Composer.Input.Text = "/exit";
                    app.InjectKey(Key.Enter); stage++; return false;
                }
                return true;
            }
            catch (Exception error) { failure = error; app.RequestStop(); return false; }
        });
        var exit = await commands.RunPreparedChatAsync(new ChatCommand(path, session, "standard", false), bootstrap, deadline.Token);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(0, exit); Assert.Equal(4, stage);
        Assert.Equal(2, questions); Assert.Equal(0, mutations);
    }
}
