using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Inline question card: what the user is told, and the bounded surface that
/// tells it. The wire contract is pinned here too: the durable
/// <c>input.requested</c> fields, the resolve route, and the single controller
/// path that reaches it.
/// </summary>
public sealed class AgentQuestionUiContractTests
{
    private const string RunId = "run_12345678901234567890123456789012";
    private const string InputId = "inp_cccccccccccccccccccccccccccccccc";

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            return Task.FromResult(Responder(request));
        }
    }

    private static PublicHostSession MakeSession() =>
        PublicHostSession.FromDescriptor(new RuntimeDescriptor(
            RuntimeDescriptor.CurrentProtocolVersion,
            "http://127.0.0.1:5180/",
            "test-token",
            Environment.ProcessId,
            DateTime.UtcNow.ToString("O"),
            Guid.NewGuid().ToString("N"),
            DateTime.UtcNow.ToString("O")));

    [Fact]
    public void The_question_card_is_hosted_by_the_chat_view()
    {
        Assert.Contains(
            GuiSource.Xml("Views", "ChatView.xaml").Descendants(),
            element => element.Name.LocalName == "AgentQuestionPanelControl");
    }

    [Fact]
    public void The_panel_carries_exactly_the_question_surface()
    {
        // One panel, its question, the optional option strip, the free-text answer,
        // and the one submit decision.
        var named = GuiSource.NamedElements(GuiSource.Xml("Controls", "AgentQuestionPanelControl.xaml"));

        foreach (var required in new[]
                 {
                     "Panel", "QuestionText", "OptionsList", "AnswerTextBox", "SubmitButton",
                 })
        {
            Assert.True(named.ContainsKey(required), $"{required} must exist.");
        }
    }

    [Fact]
    public void Each_option_names_itself_for_screen_readers()
    {
        // A repeated "Answer with this option" tells a screen reader nothing;
        // the button's accessible name is the option it submits.
        var document = GuiSource.Xml("Controls", "AgentQuestionPanelControl.xaml");
        var optionButton = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Button"
            && string.Equals(
                element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Click")?.Value,
                "OnOptionClick",
                StringComparison.Ordinal));

        Assert.NotNull(optionButton);
        Assert.Equal(
            "{Binding}",
            GuiSource.AttributeValue(optionButton!, "Name"));
    }

    [Fact]
    public void The_question_text_announces_updates_politely()
    {
        // The question is what changes when input is requested; a polite live region
        // is how a screen reader learns the agent is waiting.
        var question = GuiSource.NamedElements(GuiSource.Xml("Controls", "AgentQuestionPanelControl.xaml"))["QuestionText"];
        Assert.Equal("Polite", GuiSource.AttributeValue(question, "LiveSetting"));
    }

    [Fact]
    public void The_question_prompt_reports_the_answer_instead_of_submitting()
    {
        // The control raises the answer; the view is what talks to the run
        // controller, so the prompt cannot grow its own transport.
        var answer = typeof(RuApp.Controls.AgentQuestionPanelControl).GetEvent("AnswerRequested");

        Assert.NotNull(answer);
        var args = answer!.EventHandlerType!.GetMethod("Invoke")!.GetParameters()[1].ParameterType;
        Assert.Equal(typeof(RuApp.Controls.AgentQuestionAnsweredEventArgs), args);
        Assert.NotNull(args.GetProperty("Answer"));
    }

    [Fact]
    public void The_answer_box_is_bounded_by_the_resolve_route_limit()
    {
        // The answer is typed, so the box carries the route's own bound; a longer
        // body would only earn a rejection from the host.
        string code = GuiSource.Text("Controls", "AgentQuestionPanelControl.xaml.cs");
        string constructor = GuiSource.MethodBody(code, "AgentQuestionPanelControl");

        Assert.Contains("MaximumAnswerLength", constructor, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hostile_question_cannot_grow_the_panel()
    {
        string rendered = AgentQuestionPresentation.RenderQuestion(new string('x', 5000));

        Assert.True(rendered.Length <= AgentQuestionPresentation.MaximumQuestionLength + 1);
        Assert.EndsWith("…", rendered, StringComparison.Ordinal);

        string multiline = AgentQuestionPresentation.RenderQuestion("line one\r\nline two");
        Assert.DoesNotContain('\n', multiline);
        Assert.DoesNotContain('\r', multiline);
    }

    [Fact]
    public void An_empty_question_still_has_a_line()
    {
        Assert.Equal(AgentQuestionPresentation.FallbackQuestion, AgentQuestionPresentation.RenderQuestion(null));
        Assert.Equal(AgentQuestionPresentation.FallbackQuestion, AgentQuestionPresentation.RenderQuestion("   "));
    }

    [Fact]
    public void The_option_strip_is_bounded()
    {
        var options = Enumerable.Range(0, 20).Select(_ => new string('o', 300)).ToList();

        var rendered = AgentQuestionPresentation.RenderOptions(options);

        Assert.Equal(AgentQuestionPresentation.MaximumOptions, rendered.Count);
        Assert.All(rendered, option =>
            Assert.True(option.Length <= AgentQuestionPresentation.MaximumOptionLength + 1));
    }

    [Fact]
    public void Blank_options_are_dropped_instead_of_becoming_empty_buttons()
    {
        var rendered = AgentQuestionPresentation.RenderOptions(new[] { "keep", "   ", string.Empty });

        Assert.Equal(new[] { "keep" }, rendered);
    }

    [Fact]
    public void The_answer_contract_is_one_to_eight_thousand_characters()
    {
        Assert.False(AgentQuestionPresentation.IsAnswerWithinBounds(null));
        Assert.False(AgentQuestionPresentation.IsAnswerWithinBounds(string.Empty));
        Assert.True(AgentQuestionPresentation.IsAnswerWithinBounds("x"));
        Assert.True(AgentQuestionPresentation.IsAnswerWithinBounds(
            new string('x', AgentQuestionPresentation.MaximumAnswerLength)));
        Assert.False(AgentQuestionPresentation.IsAnswerWithinBounds(
            new string('x', AgentQuestionPresentation.MaximumAnswerLength + 1)));
    }

    [Fact]
    public void The_input_request_payload_carries_the_id_question_and_options()
    {
        // The durable input.requested event carries only the payload ref; the
        // question itself lives in the payload blob, exactly like usage reports.
        var request = AgentInputRequest.Parse(JsonSerializer.Serialize(new
        {
            input_id = InputId,
            question = "Which file should I edit?",
            options = new[] { "a.cs", "b.cs" },
        }));

        Assert.NotNull(request);
        Assert.Equal(InputId, request!.InputId);
        Assert.Equal("Which file should I edit?", request.Question);
        Assert.Equal(new[] { "a.cs", "b.cs" }, request.Options);
    }

    [Fact]
    public void A_malformed_input_payload_is_ignored()
    {
        Assert.Null(AgentInputRequest.Parse(null));
        Assert.Null(AgentInputRequest.Parse(string.Empty));
        Assert.Null(AgentInputRequest.Parse("not json"));
        Assert.Null(AgentInputRequest.Parse("{\"question\":\"no id\"}"));
    }

    [Fact]
    public void A_hostile_option_list_cannot_grow_the_payload_read()
    {
        var request = AgentInputRequest.Parse(JsonSerializer.Serialize(new
        {
            input_id = InputId,
            question = "Which file?",
            options = Enumerable.Range(0, 20).Select(index => "option " + index).ToArray(),
        }));

        Assert.NotNull(request);
        Assert.Equal(AgentQuestionPresentation.MaximumOptions, request!.Options.Count);
    }

    [Fact]
    public async Task The_submit_posts_the_answer_to_the_input_route()
    {
        // The one resolve surface: POST /agent/v1/runs/{runId}/inputs/{inputId}
        // with the bearer the rest of the Agent API uses.
        var handler = new FakeHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            },
        };
        using var http = new HttpClient(handler);
        var adapter = new AgentRunClientAdapter(MakeSession(), http);

        await adapter.SubmitInputAsync(RunId, InputId, "the answer");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method.Method);
        Assert.Equal(
            $"http://127.0.0.1:5180/agent/v1/runs/{RunId}/inputs/{InputId}",
            request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
        Assert.Equal("{\"answer\":\"the answer\"}", Assert.Single(handler.Bodies));
    }

    [Fact]
    public async Task The_submit_surfaces_the_hosts_error_code()
    {
        var handler = new FakeHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(
                    "{\"error_code\":\"input_already_resolved\"}",
                    Encoding.UTF8,
                    "application/json"),
            },
        };
        using var http = new HttpClient(handler);
        var adapter = new AgentRunClientAdapter(MakeSession(), http);

        var error = await Assert.ThrowsAsync<AgentApiException>(() =>
            adapter.SubmitInputAsync(RunId, InputId, "late"));

        Assert.Equal("input_already_resolved", error.ErrorCode);
    }

    [Fact]
    public async Task The_submit_refuses_an_empty_answer()
    {
        using var http = new HttpClient(new FakeHttpHandler());
        var adapter = new AgentRunClientAdapter(MakeSession(), http);

        var error = await Assert.ThrowsAsync<AgentApiException>(() =>
            adapter.SubmitInputAsync(RunId, InputId, string.Empty));

        Assert.Equal("invalid_answer", error.ErrorCode);
    }

    [Fact]
    public void The_submit_travels_through_the_shared_controller_and_client()
    {
        // The view talks to the controller, and the controller to the one run
        // client; there is no second input transport.
        Assert.NotNull(typeof(AgentRunController).GetMethod(nameof(AgentRunController.SubmitInputAsync)));
        string viewBody = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "OnQuestionAnswerRequested");

        Assert.Contains("SubmitInputAsync", viewBody, StringComparison.Ordinal);

        // The adapter delegates to the shared Agent API client, the same one the
        // approval decision and the CLI answer through.
        string adapterBody = GuiSource.MethodBody(
            GuiSource.Text("Services", "AgentRunClientAdapter.cs"),
            "SubmitInputAsync");

        Assert.Contains("AnswerAsync", adapterBody, StringComparison.Ordinal);
    }

    [Fact]
    public void The_question_panel_takes_the_keyboard_once_per_new_question()
    {
        // UpdateAgentInlineState reruns on every refresh while a run streams; the
        // focus move must be gated by the input id transition, exactly like the
        // approval prompt's.
        string body = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "UpdateAgentInlineState");

        int guard = body.IndexOf("_focusedInputId", StringComparison.Ordinal);
        int focus = body.IndexOf("FocusAnswer", StringComparison.Ordinal);
        Assert.True(guard >= 0, "The input id transition must be tracked.");
        Assert.True(focus > guard, "FocusAnswer must be gated by the input id transition.");
    }

    [Fact]
    public void A_restored_waiting_input_run_rearms_the_card_from_the_run_log()
    {
        // The input.requested event sits behind the adoption cursor, so the open
        // path replays the run's durable events and restores the question before
        // the monitor starts.
        string body = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "TryAdoptSessionRunAsync");

        int waiting = body.IndexOf("waiting_input", StringComparison.Ordinal);
        int replay = body.IndexOf("GetPendingRunInputAsync", StringComparison.Ordinal);
        int restore = body.IndexOf("RestorePendingInput", StringComparison.Ordinal);

        Assert.True(waiting >= 0, "The adoption must recognize a waiting_input run.");
        Assert.True(replay > waiting, "The run log must be replayed for a waiting_input run.");
        Assert.True(restore > replay, "The replayed question must be restored before the monitor starts.");
    }

    [Fact]
    public void Escape_leaves_a_pending_question_unanswered_and_the_run_waiting()
    {
        // The question card has no deny: Escape resolves as nothing, not as StopRun,
        // and the run keeps waiting for the answer.
        Assert.Equal(
            EscapeOutcome.None,
            AgentRunKeyboardPolicy.Resolve(runInHand: true, approvalShowing: false, questionShowing: true));
        Assert.Equal(
            EscapeOutcome.DenyApproval,
            AgentRunKeyboardPolicy.Resolve(runInHand: true, approvalShowing: true, questionShowing: true));
    }

    [Fact]
    public void The_replay_policy_finds_the_newest_pending_question_payload()
    {
        string older = InputReplayLine("payload_old", 3);
        string newer = InputReplayLine("payload_new", 5);

        var payloadRef = AgentInputReplayPolicy.FindPendingInputPayloadRef(
            new[] { "noise", older, "data: " + newer });

        Assert.Equal("payload_new", payloadRef);
    }

    [Fact]
    public void The_replay_policy_ignores_runs_that_never_asked()
    {
        Assert.Null(AgentInputReplayPolicy.FindPendingInputPayloadRef(
            new[] { "{\"event_type\":\"run.state_changed\"}" }));
        Assert.Null(AgentInputReplayPolicy.FindPendingInputPayloadRef(Array.Empty<string>()));
    }

    private static string InputReplayLine(string payloadRef, long sequence) =>
        JsonSerializer.Serialize(new
        {
            event_type = "input.requested",
            sequence,
            payload_ref = payloadRef,
        });
}
