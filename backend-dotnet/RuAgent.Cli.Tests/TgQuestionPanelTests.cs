using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgQuestionPanelTests
{
    private static void Drive(Action<TgChatWindow, IApplication> script)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () => { window.FocusComposer(); script(window, app); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(800), () => { app.RequestStop(); return false; });
        app.Run(window);
    }

    [Fact]
    public void Suggestions_fill_editor_and_only_enter_sends_then_restores_composer()
    {
        string? answer = null;
        Drive((window, app) =>
        {
            window.Composer.Input.Text = "unfinished prompt";
            window.Composer.Input.InsertionPoint = 4;
            window.PanelConfirmed += value => answer = value;
            window.ShowQuestion(ChatInputWorkflowTests.Question);
            app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
            {
                Assert.Contains("Which environment?", string.Join('\n', TgSnapshot.Capture(app)));
                app.InjectKey(Key.Tab);
                Assert.Equal("staging", window.QuestionPanel.Editor.Text);
                Assert.Null(answer);
                app.InjectKey(Key.Enter);
                Assert.Equal("staging", answer);
                Assert.False(window.IsModalPanelOpen);
                Assert.Equal("unfinished prompt", window.Composer.Input.Text);
                Assert.Equal(4, window.Composer.Input.InsertionPoint);
                Assert.True(window.Composer.Input.HasFocus);
                app.RequestStop(); return false;
            });
        });
    }

    [Fact]
    public void Empty_enter_does_not_answer_and_escape_defers_without_cancelling_the_run()
    {
        var confirmed = 0; var deferred = 0;
        Drive((window, app) =>
        {
            window.PanelConfirmed += _ => confirmed++;
            window.PanelCancelled += () => deferred++;
            window.ShowQuestion(ChatInputWorkflowTests.Question);
            app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
            {
                app.InjectKey(Key.Enter);
                Assert.True(window.IsModalPanelOpen);
                Assert.Equal(0, confirmed);
                app.InjectKey(Key.Esc);
                Assert.False(window.IsModalPanelOpen);
                Assert.Equal(1, deferred);
                app.RequestStop(); return false;
            });
        });
    }

    [Fact]
    public void Free_text_paste_unicode_and_newlines_are_preserved_and_oversize_is_rejected()
    {
        Drive((window, app) =>
        {
            string? answer = null;
            window.PanelConfirmed += value => answer = value;
            window.ShowQuestion(ChatInputWorkflowTests.Question with { Options = [] });
            app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
            {
                Assert.True(window.TryPasteText("Tiếng Việt 🐉"));
                app.InjectKey(Key.J.WithCtrl);
                Assert.True(window.TryPasteText("/exit"));
                var text = window.QuestionPanel.Editor.Text;
                Assert.Equal("Tiếng Việt 🐉\n/exit", text);
                Assert.False(window.QuestionPanel.Paste(new string('x', 8193)));
                Assert.Equal(text, window.QuestionPanel.Editor.Text);
                app.InjectKey(Key.Enter);
                Assert.Equal(text, answer);
                app.RequestStop(); return false;
            });
        });
    }

    [Fact]
    public void Paused_question_can_be_reopened_with_follow_without_attaching_the_draft()
    {
        Assert.True(ChatSubmissionSnapshot.IsPausedControlCommand("/follow"));
        Assert.True(ChatSubmissionSnapshot.IsPausedControlCommand("/follow run_example"));
        Assert.False(ChatSubmissionSnapshot.IsPausedControlCommand("new model prompt"));
    }

    [Fact]
    public void Native_question_request_sends_two_answers_on_the_same_run_without_deduplicating_the_second_input()
    {
        var posts = new List<string>();
        using var http = new HttpClient(new ChatInputWorkflowTests.Handler(async request =>
        {
            if (request.Method == HttpMethod.Get) return ChatInputWorkflowTests.Json(ChatInputWorkflowTests.Summary());
            var id = request.RequestUri!.Segments.Last();
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("staging", body.RootElement.GetProperty("answer").GetString());
            posts.Add(id);
            return ChatInputWorkflowTests.Json(System.Text.Json.JsonSerializer.Serialize(new { input_id = id, status = "answered" }));
        }));
        using var api = new RuAgent.Client.AgentApiClient(ChatInputWorkflowTests.Session, http);
        var workflow = new ChatInputWorkflow(api);
        Task? work = null;
        Drive((window, app) =>
        {
            work = Task.Run(async () =>
            {
                foreach (var id in new[] { ChatInputWorkflowTests.InputId, "inp_" + new string('c', 32) })
                    Assert.Equal(ChatInputOutcome.Answered, await workflow.AnswerAsync(ChatInputWorkflowTests.Question with { InputId = id },
                        (question, ct) => TgPanelRequests.RequestQuestionAsync(window, question, ct), default));
            });
            app.AddTimeout(TimeSpan.FromMilliseconds(30), () =>
            {
                if (work.IsCompleted) { work.GetAwaiter().GetResult(); app.RequestStop(); return false; }
                if (window.IsModalPanelOpen) { app.InjectKey(Key.Tab); app.InjectKey(Key.Enter); }
                return true;
            });
        });
        Assert.NotNull(work);
        Assert.True(work.IsCompletedSuccessfully);
        Assert.Equal(new[] { ChatInputWorkflowTests.InputId, "inp_" + new string('c', 32) }, posts);
    }

    [Fact]
    public void Already_cancelled_request_never_leaves_an_orphan_modal()
    {
        Task<string?>? request = null;
        Drive((window, app) =>
        {
            request = TgPanelRequests.RequestQuestionAsync(window, ChatInputWorkflowTests.Question, new CancellationToken(true));
            app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
            {
                Assert.True(request.IsCompletedSuccessfully);
                Assert.Null(request.Result);
                Assert.False(window.IsModalPanelOpen);
                app.RequestStop(); return false;
            });
        });
    }

    [Fact]
    public void Long_question_pages_and_reverse_tab_starts_at_last_option()
    {
        Drive((window, app) =>
        {
            window.ShowQuestion(ChatInputWorkflowTests.Question with
            {
                Question = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"Question row {i:D2}")),
                Options = Enumerable.Range(1, 8).Select(i => $"option {i}").ToArray(),
            });
            app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
            {
                app.InjectKey(Key.Tab.WithShift);
                Assert.Equal("option 8", window.QuestionPanel.Editor.Text);
                app.InjectKey(Key.PageDown); app.InjectKey(Key.PageDown); app.InjectKey(Key.PageDown);
                app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
                {
                    var screen = string.Join('\n', TgSnapshot.Capture(app));
                    Assert.Contains("Question row 20", screen);
                    Assert.Contains("option 8", screen);
                    Assert.DoesNotContain('\u001b', screen);
                    app.RequestStop(); return false;
                });
                return false;
            });
        });
    }
}
