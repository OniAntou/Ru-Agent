using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatDraftSessionTests
{
    private sealed class Host : HttpMessageHandler
    {
        internal List<string> Requests = [];
        internal List<string?> PolicySessions = [];
        internal bool FailCreate;
        internal static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath; Requests.Add(request.Method + " " + path);
            if (path == "/sessions/new")
            {
                if (FailCreate) throw new HttpRequestException("synthetic create response lost");
                return Task.FromResult(Json("""{"status":"ok","session_id":"sess_first"}"""));
            }
            if (path.Contains("/policies")) return ReadPolicyAsync(request, ct);
            if (path.EndsWith("/preset")) return Task.FromResult(Json("{}", HttpStatusCode.ServiceUnavailable));
            if (path == "/settings") return Task.FromResult(Json("""{"status":"ok","backend":"OpenAI","openai_model":"model-a"}"""));
            if (path.EndsWith("/activate")) return Task.FromResult(Json("""{"status":"ok","session":{"id":"sess_first","title":"New","messages":[]}}"""));
            if (path == "/models-dev") return Task.FromResult(Json("""{"status":"ok","providers":[]}"""));
            return Task.FromResult(Json("""{"status":"ok","items":[],"models":[],"provider_ids":[]}"""));
        }

        private async Task<HttpResponseMessage> ReadPolicyAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
            {
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
                PolicySessions.Add(body.RootElement.GetProperty("session_id").GetString());
            }
            return Json("{}", HttpStatusCode.ServiceUnavailable);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("/exit\n")]
    [InlineData("/model\n/provider\n/status\n/help\n/preset plan\n/tasks\n/changes\n/rewind\n/subagents\n/follow\n/exit\n")]
    public async Task Opening_chat_and_local_slash_commands_create_no_saved_session(string input)
    {
        using var host = new Host(); using var http = new HttpClient(host);
        var output = new StringWriter(); var errors = new StringWriter();
        using var commands = new AgentCommands(ChatRewindWorkflowTests.Host.Session, http, output, errors, new StringReader(input));
        Assert.Equal(0, await commands.ChatAsync(new ChatCommand(Path.GetTempPath(), null, "standard", false), default));
        Assert.DoesNotContain(host.Requests, request => request.Contains("/sessions/") || request.Contains("/agent/v1/"));
        if (input.Contains("/status")) Assert.Contains("not started", output.ToString());
    }

    [Fact]
    public async Task First_real_prompt_materializes_once_before_preset_and_reuses_that_session()
    {
        using var host = new Host(); using var http = new HttpClient(host);
        using var commands = new AgentCommands(ChatRewindWorkflowTests.Host.Session, http, new StringWriter(), new StringWriter(),
            new StringReader("/model\nfirst task\nsecond task\n/exit\n"));
        Assert.Equal(0, await commands.ChatAsync(new ChatCommand(Path.GetTempPath(), null, "standard", false), default));
        Assert.Single(host.Requests, request => request == "POST /sessions/new");
        var create = host.Requests.IndexOf("POST /sessions/new");
        Assert.Contains(host.Requests.Skip(create + 1), request => request.Contains("/policies"));
        Assert.NotEmpty(host.PolicySessions); Assert.All(host.PolicySessions, id => Assert.Equal("sess_first", id));
        Assert.DoesNotContain(host.Requests, request => request.Contains("/sessions//"));
    }

    [Fact]
    public async Task Failed_first_create_does_not_retry_or_start_a_run_on_the_next_input()
    {
        using var host = new Host { FailCreate = true }; using var http = new HttpClient(host); var errors = new StringWriter();
        using var commands = new AgentCommands(ChatRewindWorkflowTests.Host.Session, http, new StringWriter(), errors,
            new StringReader("first task\nsecond task\n/exit\n"));
        Assert.Equal(0, await commands.ChatAsync(new ChatCommand(Path.GetTempPath(), null, "standard", false), default));
        Assert.Single(host.Requests, request => request == "POST /sessions/new");
        Assert.DoesNotContain(host.Requests, request => request.Contains("/agent/v1/")); Assert.Contains("session_create_failed", errors.ToString());
    }

    [Fact]
    public async Task Explicit_new_still_creates_exactly_one_session()
    {
        using var host = new Host(); using var http = new HttpClient(host);
        using var commands = new AgentCommands(ChatRewindWorkflowTests.Host.Session, http, new StringWriter(), new StringWriter(), new StringReader("/new\n/exit\n"));
        Assert.Equal(0, await commands.ChatAsync(new ChatCommand(Path.GetTempPath(), null, "standard", false), default));
        Assert.Single(host.Requests, request => request == "POST /sessions/new");
    }

    [Fact]
    public void Queued_draft_binding_changes_only_the_initial_session_and_preserves_request_identity()
    {
        var draft = ChatSubmissionSnapshot.Create("second", "", "w", "standard", "b", "m", [], []);
        var bound = draft.BindInitialDraft("saved", "saved");
        Assert.Equal("saved", bound.SessionId); Assert.Equal(draft.IdempotencyKey, bound.IdempotencyKey);
        Assert.Equal(draft.Prompt, bound.Prompt); Assert.Equal(draft.Images, bound.Images); Assert.Equal(draft.Texts, bound.Texts);
        Assert.Same(draft, draft.BindInitialDraft("other", "saved"));
        var existing = draft with { SessionId = "existing" };
        Assert.Same(existing, existing.BindInitialDraft("saved", "saved"));
    }

    [Fact]
    public void Native_model_command_preserves_welcome_without_user_card_or_working_status()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build(); var loop = new TgChatLoop(window);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.SetPromptHandler(async (_, _, ct) => await release.Task.WaitAsync(ct));
        var step = 0; Exception? failure = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(120), () =>
        {
            try
            {
                if (step == 0) { window.FocusComposer(); window.Composer.Input.Text = "/model"; loop.Submit(); step++; return true; }
                Assert.True(window.Landing.Visible); Assert.Equal(TgHintsState.Idle, window.Hints.ActiveState);
                step++; release.TrySetResult(); loop.Stop(); app.RequestStop(); return false;
            }
            catch (Exception ex) { failure = ex; release.TrySetResult(); loop.Stop(); app.RequestStop(); return false; }
        });
        app.AddTimeout(TimeSpan.FromSeconds(5), () => { release.TrySetResult(); loop.Stop(); app.RequestStop(); return false; });
        app.Run(window);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(2, step);
    }
}
