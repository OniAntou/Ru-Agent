using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Net;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Model;
using Xunit;

namespace RuAgent.Runtime.Model.Tests;

public sealed partial class ModelStepServiceTests
{
    [Fact]
    public async Task Measured_request_parts_attach_to_usage_without_replacing_provider_counts()
    {
        var service = new ModelStepService(new OpenAiCompatibleModelProvider(new HttpClient(new ContextUsageHandler())));
        var bootstrap = new ProviderBootstrapData("fixture", ProviderProtocols.ChatCompletions,
            new Uri("https://provider.invalid/v1/chat/completions"), "fixture-model", "synthetic");
        var prepared = service.PrepareRequest(Request(bootstrap: bootstrap));
        var result = await service.RunPreparedAsync(prepared, null, CancellationToken.None);
        Assert.NotNull(prepared.ContextTokens);
        Assert.True(prepared.ContextTokens.SystemPrompt > 0);
        Assert.Equal(prepared.ContextTokens, result.Usage!.ContextTokens);
        Assert.Equal(321, result.Usage.InputTokens);
    }

    [Fact]
    public void Pinned_session_header_travels_on_the_generated_request()
    {
        var service = new ModelStepService(new OpenAiCompatibleModelProvider(new HttpClient()));
        var bootstrap = new ProviderBootstrapData("models.dev:opencode-go", ProviderProtocols.ChatCompletions,
            new Uri("https://opencode.ai/inference/go/openai/v1/chat/completions"), "glm-5.3-flash", "synthetic",
            maximumOutputTokens: 8192, contextWindowTokens: 128_000, outputTokenReserve: 8192,
            sessionHeaderName: "x-opencode-session");

        var session = new SessionId("0123456789abcdef0123456789abcdef");
        var prepared = service.PrepareRequest(Request(bootstrap: bootstrap, sessionId: session));

        Assert.Equal(session.Value, prepared.Request.ProviderHeaders["x-opencode-session"]);
        Assert.StartsWith("RuAgent/", prepared.Request.ProviderHeaders["User-Agent"]);
        // A provider that pins no session header sends none.
        Assert.Empty(service.PrepareRequest(Request()).Request.ProviderHeaders);
    }

    [Fact]
    public void Pinned_session_header_without_a_conversation_fails_closed()
    {
        var service = new ModelStepService(new OpenAiCompatibleModelProvider(new HttpClient()));
        var bootstrap = new ProviderBootstrapData("models.dev:opencode-go", ProviderProtocols.ChatCompletions,
            new Uri("https://opencode.ai/inference/go/openai/v1/chat/completions"), "glm-5.3-flash", "synthetic",
            maximumOutputTokens: 8192, contextWindowTokens: 128_000, outputTokenReserve: 8192,
            sessionHeaderName: "x-opencode-session");

        var error = Assert.Throws<ModelStepException>(() => service.PrepareRequest(Request(bootstrap: bootstrap)));

        Assert.Equal("provider_session_missing", error.Code);
        Assert.False(error.Retryable);
    }

    private sealed class ContextUsageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":321,\"completion_tokens\":1,\"total_tokens\":322}}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            });
    }

    [Fact]
    public async Task Prepared_projection_is_the_exact_dispatched_body_and_counts_images_separately()
    {
        var provider = new ProjectingFakeProvider();
        var service = new ModelStepService(provider);
        var bootstrap = new ProviderBootstrapData("fixture", ProviderProtocols.ChatCompletions,
            new Uri("https://provider.invalid/v1/chat/completions"), "fixture-model",
            "credential-must-not-enter-body", maximumOutputTokens: 4096,
            contextWindowTokens: 32_000, outputTokenReserve: 4096, acceptsImageInput: true);
        var plain = service.PrepareRequest(Request(bootstrap: bootstrap), requireExactProjection: true);
        var imageRequest = Request(bootstrap: bootstrap,
            images: [new ModelImage("image/png", new string('A', 200_000))]);
        var withImage = service.PrepareRequest(imageRequest, requireExactProjection: true);

        Assert.True(withImage.SerializedBodyBytes > 200_000);
        Assert.InRange(withImage.EstimatedInputTokens - plain.EstimatedInputTokens, 4096, 4112);
        Assert.Equal(4096, plain.OutputTokenCap);

        var dispatched = await service.RunPreparedAsync(plain, null, CancellationToken.None);

        Assert.Equal("prepared", dispatched.Text);
        Assert.Equal(plain.SerializedBodySha256, provider.DispatchedBodySha256);
        Assert.Equal(plain.SerializedBodyBytes, provider.DispatchedBodyBytes);
    }

    [Fact]
    public async Task Actual_chat_wire_keeps_current_prompt_after_work_state_and_accounts_for_both()
    {
        const string tail = "CURRENT-PROMPT-TAIL-8c1b";
        var currentPrompt = new string('p', 15_900 - tail.Length) + tail;
        var state = new string('s', 15_500);
        var handler = new RecordingHttpHandler();
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));
        var service = new ModelStepService(provider);
        var bootstrap = new ProviderBootstrapData("fixture", ProviderProtocols.ChatCompletions,
            new Uri("https://provider.invalid/v1/chat/completions"), "fixture-model", "synthetic",
            maximumOutputTokens: 4096, contextWindowTokens: 32_000, outputTokenReserve: 4096);
        var withoutState = service.PrepareRequest(Request(prompt: currentPrompt, bootstrap: bootstrap), requireExactProjection: true);
        var withState = service.PrepareRequest(Request(prompt: currentPrompt, bootstrap: bootstrap,
            modelMessages: [new ModelTurn("user", state)]), requireExactProjection: true);

        Assert.True(withState.EstimatedInputTokens > withoutState.EstimatedInputTokens);
        Assert.True(withState.SerializedBodyBytes > withoutState.SerializedBodyBytes);
        _ = await service.RunPreparedAsync(withState, null, CancellationToken.None);

        Assert.Equal(withState.SerializedBodySha256, handler.BodySha256);
        Assert.Equal(withState.SerializedBodyBytes, handler.BodyBytes);
        var messages = JsonNode.Parse(handler.Body!)!["messages"]!.AsArray();
        Assert.Equal(state, messages.Single(message => message!["content"]?.GetValue<string>() == state)!["content"]!.GetValue<string>());
        Assert.Equal(currentPrompt, messages[^1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Responses_native_step_requires_exact_bounded_continuation_for_normalized_call_batch()
    {
        var bootstrap = ResponsesNativeBootstrap();
        var continuation = new ProviderContinuation("openai", "openai_responses", "gpt-5.6-luna", 1,
        [
            new JsonObject { ["type"] = "reasoning", ["id"] = "reason-1", ["encrypted_content"] = "opaque" },
            new JsonObject { ["type"] = "function_call", ["id"] = "item-1", ["call_id"] = "call-1", ["name"] = "files.read", ["arguments"] = "{\"path\":\"a.txt\"}" },
        ]);
        var provider = new FakeModelProvider(new ModelDelta(null,
            [new ModelToolFragment("call-1", "files.read", "{\"path\":\"a.txt\"}")], true, "tool_calls",
            providerContinuation: continuation));

        var result = await new ModelStepService(provider).RunAsync(Request(includeTool: true, bootstrap: bootstrap), CancellationToken.None);

        Assert.Equal(continuation.SerializedOutputItems.ToArray(), result.ProviderContinuation!.SerializedOutputItems.ToArray());
        Assert.Equal("call-1", Assert.Single(result.ToolCalls).ProviderCallId);
    }

    [Fact]
    public async Task Anthropic_native_step_returns_tool_call_but_never_surfaces_signed_thinking()
    {
        var bootstrap = new ProviderBootstrapData(
            "Anthropic", ProviderProtocols.AnthropicMessages, new Uri("https://provider.invalid/v1/messages"),
            "claude-fable-5", "synthetic", maximumOutputTokens: 8192);
        var continuation = new ProviderContinuation("anthropic", "anthropic_messages", "claude-fable-5", 1,
        [
            new JsonObject { ["type"] = "text", ["text"] = "I will inspect." },
            new JsonObject { ["type"] = "thinking", ["thinking"] = "private thought", ["signature"] = "opaque-signature" },
            new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_read_1", ["name"] = "files.read", ["input"] = JsonNode.Parse("{\"path\":\"a.txt\"}") },
        ]);
        var provider = new FakeModelProvider(
            new ModelDelta("I will inspect.", [], false, null),
            new ModelDelta(null, [new ModelToolFragment("toolu_read_1", "files.read", "{\"path\":\"a.txt\"}")], true, "tool_calls", providerContinuation: continuation));

        var result = await new ModelStepService(provider).RunAsync(
            Request(includeTool: true, bootstrap: bootstrap), CancellationToken.None);

        Assert.Equal("I will inspect.", result.Text);
        Assert.Null(result.ReasoningText);
        Assert.Equal("opaque-signature", result.ProviderContinuation!.OutputItems[1]["signature"]!.GetValue<string>());
        Assert.Equal("toolu_read_1", Assert.Single(result.ToolCalls).ProviderCallId);
        var modelRequest = Assert.Single(provider.Requests);
        Assert.Empty(modelRequest.AnthropicMessages);
    }

    [Fact]
    public async Task Responses_native_step_rejects_missing_or_mismatched_continuation_before_dispatch_result()
    {
        var bootstrap = ResponsesNativeBootstrap();
        var provider = new FakeModelProvider(new ModelDelta(null,
            [new ModelToolFragment("call-1", "files.read", "{\"path\":\"a.txt\"}")], true, "tool_calls"));
        var missing = await Assert.ThrowsAsync<ModelStepException>(() =>
            new ModelStepService(provider).RunAsync(Request(includeTool: true, bootstrap: bootstrap), CancellationToken.None));
        Assert.Equal("provider_continuation_missing", missing.Code);

        var mismatch = new ProviderContinuation("openai", "openai_responses", "gpt-5.6-luna", 1,
        [new JsonObject { ["type"] = "function_call", ["id"] = "item-1", ["call_id"] = "other-call", ["name"] = "files.read", ["arguments"] = "{\"path\":\"a.txt\"}" }]);
        var mismatchedProvider = new FakeModelProvider(new ModelDelta(null,
            [new ModelToolFragment("call-1", "files.read", "{\"path\":\"a.txt\"}")], true, "tool_calls", providerContinuation: mismatch));
        var invalid = await Assert.ThrowsAsync<ModelStepException>(() =>
            new ModelStepService(mismatchedProvider).RunAsync(Request(includeTool: true, bootstrap: bootstrap), CancellationToken.None));
        Assert.Equal("protocol_failure", invalid.Code);
    }

    private static ProviderBootstrapData ResponsesNativeBootstrap() => new(
        "OpenAI", ProviderProtocols.OpenAiResponses, new Uri("https://provider.invalid/v1/responses"),
        "gpt-5.6-luna", "synthetic");

    [Fact]
    public async Task Step_service_composes_once_and_returns_one_done_with_receipt()
    {
        var provider = new FakeModelProvider(
            new ModelDelta("xin ", Array.Empty<ModelToolFragment>(), false, null),
            new ModelDelta("chao anh", Array.Empty<ModelToolFragment>(), true, "stop"));
        var result = await new ModelStepService(provider).RunAsync(Request(), CancellationToken.None);

        Assert.Single(provider.Requests);
        Assert.Equal("xin chao anh", result.Text);
        Assert.Equal("stop", result.FinishReason);
        Assert.Equal(SystemPromptReceipt.SupportedComposerVersion, result.PromptReceipt.ComposerVersion);
        Assert.Equal("## ru.persona", provider.Requests[0].SystemPrompt[..13]);
    }

    [Fact]
    public async Task Step_service_returns_provider_usage_without_synthesizing_missing_counts()
    {
        var provider = new FakeModelProvider(
            new ModelDelta("ok", Array.Empty<ModelToolFragment>(), true, "stop",
                new ModelUsage(12, 5, 17, 3, null)));

        var result = await new ModelStepService(provider).RunAsync(Request(), CancellationToken.None);

        Assert.NotNull(result.Usage);
        Assert.Equal(12, result.Usage!.InputTokens);
        Assert.Equal(5, result.Usage.OutputTokens);
        Assert.Equal(17, result.Usage.TotalTokens);
        Assert.Equal(3, result.Usage.CacheReadInputTokens);
        Assert.Null(result.Usage.CacheWriteInputTokens);
    }

    [Fact]
    public async Task Step_service_includes_images_in_model_request()
    {
        var provider = new FakeModelProvider(
            new ModelDelta("mo ta", Array.Empty<ModelToolFragment>(), true, "stop"));
        var request = Request(images: [new ModelImage("image/png", "aGVsbG8=")]);
        var result = await new ModelStepService(provider).RunAsync(request, CancellationToken.None);

        Assert.Equal("mo ta", result.Text);
        Assert.Single(provider.Requests);
        Assert.Single(provider.Requests[0].Images);
        Assert.Equal("image/png", provider.Requests[0].Images[0].MimeType);
        Assert.Equal("aGVsbG8=", provider.Requests[0].Images[0].Base64Data);
    }

    [Fact]
    public async Task Step_service_accepts_empty_prompt_when_image_present()
    {
        var provider = new FakeModelProvider(
            new ModelDelta("anh dep", Array.Empty<ModelToolFragment>(), true, "stop"));
        var request = Request(prompt: "", images: [new ModelImage("image/jpeg", "d29ybGQ=")]);
        var result = await new ModelStepService(provider).RunAsync(request, CancellationToken.None);

        Assert.Single(provider.Requests);
        Assert.Equal("", provider.Requests[0].Prompt);
        Assert.Single(provider.Requests[0].Images);
        Assert.Equal("image/jpeg", provider.Requests[0].Images[0].MimeType);
        Assert.Equal("d29ybGQ=", provider.Requests[0].Images[0].Base64Data);
    }

    [Fact]
    public async Task Step_service_forwards_visible_deltas_without_waiting_for_done()
    {
        var provider = new FakeModelProvider(
            new ModelDelta("xin ", Array.Empty<ModelToolFragment>(), false, null),
            new ModelDelta("chao anh", Array.Empty<ModelToolFragment>(), true, "stop"));
        var deltas = new List<string>();

        var result = await new ModelStepService(provider).RunAsync(
            Request(),
            text =>
            {
                deltas.Add(text);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal("xin chao anh", result.Text);
        Assert.Equal(new[] { "xin ", "chao anh" }, deltas);
    }

    [Fact]
    public async Task Step_service_accumulates_one_tool_call_without_visible_arguments()
    {
        var provider = new FakeModelProvider(
            new ModelDelta(null, new[] { new ModelToolFragment("call-1", "files.read", "{\"path\":\"README.md\"}") }, true, "tool_calls"));

        var result = await new ModelStepService(provider).RunAsync(Request(includeTool: true), CancellationToken.None);

        // The wire contract keeps tool selections text-free; narration streams
        // live through deltas and never becomes step text.
        Assert.Null(result.Text);
        Assert.Single(result.ToolCalls);
        Assert.Equal("files.read", result.ToolCalls[0].ToolId);
        Assert.Equal("README.md", result.ToolCalls[0].Arguments.GetProperty("path").GetString());
        Assert.DoesNotContain("README.md", result.PromptReceipt.PromptSha256, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Step_service_keeps_narration_beside_a_native_call()
    {
        var provider = new FakeModelProvider(
            new ModelDelta("Dang tao file. ", Array.Empty<ModelToolFragment>(), false, null),
            new ModelDelta(
                null,
                new[] { new ModelToolFragment("call-9", "files.read", "{\"path\":\"README.md\"}") },
                true,
                "tool_calls"));

        var result = await new ModelStepService(provider).RunAsync(
            Request(includeTool: true),
            CancellationToken.None);

        // Narration streams live and it stays with the step: a reader watching the run sees
        // what the model said before it reached for the tool, not only the final answer.
        Assert.Equal("Dang tao file.", result.Text);
        Assert.Equal("tool_calls", result.FinishReason);
        Assert.Equal("files.read", Assert.Single(result.ToolCalls).ToolId);
    }

    [Fact]
    public async Task Step_service_keeps_call_markup_out_of_step_text_beside_a_native_call()
    {
        // The other side of the same rule: text that is the call itself is not narration, and
        // raw call markup must never reach the chat.
        var provider = new FakeModelProvider(
            new ModelDelta("{\"tool_id\":\"files.read\",\"arguments\":{\"path\":\"README.md\"}}", Array.Empty<ModelToolFragment>(), false, null),
            new ModelDelta(
                null,
                new[] { new ModelToolFragment("call-4", "files.read", "{\"path\":\"README.md\"}") },
                true,
                "tool_calls"));

        var result = await new ModelStepService(provider).RunAsync(
            Request(includeTool: true),
            CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Single(result.ToolCalls);
    }

    [Fact]
    public async Task Step_service_merges_streamed_native_fragments_that_carry_the_call_id_once()
    {
        var provider = new FakeModelProvider(
            new ModelDelta(null, new[] { new ModelToolFragment("call-7", "files.read", "{\"path\":") }, false, null),
            new ModelDelta(null, new[] { new ModelToolFragment(null, string.Empty, "\"README.md\"}") }, true, "tool_calls"));

        var result = await new ModelStepService(provider).RunAsync(
            Request(includeTool: true),
            CancellationToken.None);

        Assert.Equal("tool_calls", result.FinishReason);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("call-7", call.ProviderCallId);
        Assert.Equal("files.read", call.ToolId);
        Assert.Equal("README.md", call.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Step_service_marks_malformed_native_arguments_as_retryable_model_repair()
    {
        var provider = new FakeModelProvider(
            new ModelDelta(null, new[] { new ModelToolFragment("call-8", "files.read", "{\"path\":") }, true, "tool_calls"));

        var error = await Assert.ThrowsAsync<ModelStepException>(() =>
            new ModelStepService(provider).RunAsync(Request(includeTool: true), CancellationToken.None));

        Assert.Equal("model_tool_call_invalid", error.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task Step_service_accepts_parallel_native_calls_and_returns_them_in_order()
    {
        var provider = new FakeModelProvider(
            new ModelDelta(
                null,
                new[]
                {
                    new ModelToolFragment("call-1", "files.read", "{\"path\":\"READ"),
                    new ModelToolFragment(null, string.Empty, "ME.md\"}"),
                    new ModelToolFragment("call-2", "files.read", "{\"path\":\"CHANGELOG.md\"}"),
                },
                true,
                "tool_calls"));

        var result = await new ModelStepService(provider).RunAsync(Request(includeTool: true), CancellationToken.None);

        Assert.Equal(2, result.ToolCalls.Count);
        Assert.Equal("call-1", result.ToolCalls[0].ProviderCallId);
        Assert.Equal("README.md", result.ToolCalls[0].Arguments.GetProperty("path").GetString());
        Assert.Equal("call-2", result.ToolCalls[1].ProviderCallId);
        Assert.Equal("CHANGELOG.md", result.ToolCalls[1].Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Step_service_treats_a_repeated_call_id_on_a_continuation_as_one_call()
    {
        // Gateways that echo the call id on every chunk stream one call as
        // {id, name, args} then {id, "", args}. The repeated id names the call
        // already in progress, so it must not open a second, nameless group --
        // the indexed assembly accepts the same shape and the two assemblies
        // must agree on the same protocol.
        var provider = new FakeModelProvider(
            new ModelDelta(
                null,
                new[]
                {
                    new ModelToolFragment("call-1", "files.read", "{\"path\":\"READ"),
                    new ModelToolFragment("call-1", string.Empty, "ME.md\"}"),
                },
                true,
                "tool_calls"));

        var result = await new ModelStepService(provider).RunAsync(
            Request(includeTool: true),
            CancellationToken.None);

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("call-1", call.ProviderCallId);
        Assert.Equal("README.md", call.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Step_service_assembles_a_second_call_whose_arguments_arrive_without_a_call_id()
    {
        // Providers put the call id and the tool name on a call's first fragment
        // and stream its arguments after that. Once a second call had been
        // opened, an id-less fragment was rejected purely because a second
        // group existed -- so any step making two tool calls died here, which is
        // the shape the Protocol explicitly allows.
        var provider = new FakeModelProvider(
            new ModelDelta(
                null,
                new[]
                {
                    new ModelToolFragment("call-1", "files.read", "{\"path\":\"README.md\"}"),
                    new ModelToolFragment("call-2", "files.read", "{\"path\":\"CHANG"),
                    new ModelToolFragment(null, string.Empty, "E.md\"}"),
                },
                true,
                "tool_calls"));

        var result = await new ModelStepService(provider).RunAsync(
            Request(includeTool: true),
            CancellationToken.None);

        Assert.Equal(2, result.ToolCalls.Count);
        Assert.Equal("call-1", result.ToolCalls[0].ProviderCallId);
        Assert.Equal("README.md", result.ToolCalls[0].Arguments.GetProperty("path").GetString());
        Assert.Equal("call-2", result.ToolCalls[1].ProviderCallId);
        // The two argument fragments spell CHANGE.md across the boundary.
        Assert.Equal("CHANGE.md", result.ToolCalls[1].Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Step_service_still_rejects_an_idless_fragment_that_also_names_a_tool()
    {
        // The remaining ambiguity: a fragment with no call id but a tool name,
        // arriving while the current call already has data, would be a second
        // call that never got an id. Retrying that cannot help and guessing
        // could run the wrong tool, so it stays closed.
        var provider = new FakeModelProvider(
            new ModelDelta(
                null,
                new[]
                {
                    new ModelToolFragment("call-1", "files.read", "{\"path\":\"README.md\"}"),
                    new ModelToolFragment(null, "files.read", "{\"path\":\"CHANGELOG.md\"}"),
                },
                true,
                "tool_calls"));

        var error = await Assert.ThrowsAsync<ModelStepException>(() =>
            new ModelStepService(provider).RunAsync(Request(includeTool: true), CancellationToken.None));

        Assert.Equal("protocol_failure", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task A_tool_call_finish_reason_with_no_fragments_is_retryable()
    {
        // The provider announced tool_calls and streamed none. There is no
        // half-parsed call here, so a bounded retry cannot execute the wrong
        // thing -- and the Host feeds the reason back so the next attempt is
        // asked for a different shape. The ambiguous idless continuation above
        // keeps its closed-on-purpose behaviour; this is the opposite case.
        var provider = new FakeModelProvider(
            new ModelDelta(null, Array.Empty<ModelToolFragment>(), true, "tool_calls"));

        var error = await Assert.ThrowsAsync<ModelStepException>(() =>
            new ModelStepService(provider).RunAsync(Request(includeTool: true), CancellationToken.None));

        Assert.Equal("protocol_failure", error.Code);
        Assert.True(error.Retryable);
        Assert.NotNull(error.Trace);
        Assert.Contains("site=zero_fragments", error.Trace);
        Assert.Contains("finishIsToolCalls=1", error.Trace);
    }

    [Fact]
    public async Task Step_service_assigns_request_scoped_ids_to_native_calls_without_provider_ids()
    {
        var provider = new FakeModelProvider(
            new ModelDelta(
                null,
                new[]
                {
                    new ModelToolFragment(null, "files.read", "{\"path\":\"README.md\"}"),
                    new ModelToolFragment("call-2", "files.read", "{\"path\":\"CHANGELOG.md\"}"),
                },
                true,
                "tool_calls"));

        var result = await new ModelStepService(provider).RunAsync(
            Request(includeTool: true),
            onDelta: null,
            cancellationToken: CancellationToken.None,
            modelStepId: "step-1");

        Assert.Equal(2, result.ToolCalls.Count);
        Assert.Equal("ru_call_step-1_1", result.ToolCalls[0].ProviderCallId);
        Assert.Equal("call-2", result.ToolCalls[1].ProviderCallId);
        Assert.Equal("README.md", result.ToolCalls[0].Arguments.GetProperty("path").GetString());
        Assert.Equal("CHANGELOG.md", result.ToolCalls[1].Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Step_service_rejects_unadvertised_native_tool_as_retryable_model_repair()
    {
        var provider = new FakeModelProvider(
            new ModelDelta(null, new[] { new ModelToolFragment("call-3", "files.teleport", "{}") }, true, "tool_calls"));

        var error = await Assert.ThrowsAsync<ModelStepException>(() =>
            new ModelStepService(provider).RunAsync(Request(includeTool: true), CancellationToken.None));

        Assert.Equal("model_tool_call_unavailable", error.Code);
        Assert.True(error.Retryable);
    }

    // Native tool calling is the only protocol: a tool call the model writes out as text is
    // never executed and never shown as an answer. It fails retryably so the Host's bounded
    // repair asks for a native call instead. (These shapes used to execute as text-selected
    // calls; that calling mode is retired.)
    private const string HandWrittenRead = "{\"tool_id\":\"files.read\",\"tool_version\":\"1.0.0\",\"arguments\":{\"path\":\"README.md\"}}";

    public static TheoryData<string, string[]> HandWrittenToolCalls => new()
    {
        { "json envelope", [HandWrittenRead] },
        { "split json envelope", ["{\"tool_id\":\"files.", "read\",\"tool_version\":\"1.0.0\",\"arguments\":{\"path\":\"README.md\"}}"] },
        { "split wrapper", ["<tool_", "call> " + HandWrittenRead + " </tool_call>"] },
        { "fenced json", ["```json\n" + HandWrittenRead + "\n```"] },
        { "split fence prefix", ["``", "`json\n" + HandWrittenRead + "\n```"] },
        { "split unclosed wrapper", ["<tool_", "call> {\"tool_id\":\"files.read\",\"tool_version\":\"1.0.0\",\"arguments\":{\"path\":\"a\"}}"] },
        { "trailing tag dialect after prose", ["I'll look at the workspace structure first.",
            "<tool_call>read<arg_key>op</arg_key><arg_value>list</arg_value>",
            "<arg_key>path</arg_key><arg_value>/D/Test Agent</arg_value></tool_call>"] },
        { "trailing json wrapper after prose", ["I'll check the workspace structure first. ", "<tool_call>" + HandWrittenRead + "</tool_call>"] },
        { "mangled call glued to prose", ["Xin chào! Tôi sẽ thử truy cập vào folder.<tool_call>\n<(\n\"tool_id\":\"files.read\",\n\"tool_version\":\"1.0.0\",\n\"arguments\":{\"path\":\"D:\\\\Test Agent\"}\n}\n}>"] },
        { "incomplete json envelope", ["{\"tool_id\":\"files.read\"}"] },
    };

    [Theory]
    [MemberData(nameof(HandWrittenToolCalls))]
    public async Task Step_service_rejects_a_hand_written_tool_call_without_executing_or_leaking_it(string shape, string[] parts)
    {
        Assert.NotEmpty(shape);
        var provider = new FakeModelProvider(parts.Select((part, index) => index == parts.Length - 1
            ? new ModelDelta(part, Array.Empty<ModelToolFragment>(), true, "stop")
            : new ModelDelta(part, Array.Empty<ModelToolFragment>(), false, null)).ToArray());
        var deltas = new List<string>();

        var error = await Assert.ThrowsAsync<ModelStepException>(() => new ModelStepService(provider).RunAsync(
            Request(includeTool: true),
            text =>
            {
                deltas.Add(text);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None));

        Assert.Equal("invalid_selection", error.Code);
        Assert.True(error.Retryable);
        var streamed = string.Concat(deltas);
        Assert.DoesNotContain("tool_id", streamed, StringComparison.Ordinal);
        Assert.DoesNotContain("<tool_call>", streamed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("arg_key", streamed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Step_service_keeps_a_json_answer_visible_when_no_tools_are_advertised()
    {
        const string answer = "{\"status\":\"ok\",\"count\":2}";
        var provider = new FakeModelProvider(new ModelDelta(answer, Array.Empty<ModelToolFragment>(), true, "stop"));

        var result = await new ModelStepService(provider).RunAsync(Request(), CancellationToken.None);

        Assert.Equal(answer, result.Text);
        Assert.Empty(result.ToolCalls);
    }

    [Fact]
    public async Task Step_service_rejects_lone_unparseable_wrapper()
    {
        var provider = new FakeModelProvider(
            new ModelDelta("<tool_call>read<arg_key>op</arg_key><arg_value>list</arg_value></tool_call>", Array.Empty<ModelToolFragment>(), true, "stop"));

        var error = await Assert.ThrowsAsync<ModelStepException>(() =>
            new ModelStepService(provider).RunAsync(Request(includeTool: true), CancellationToken.None));

        Assert.Equal("invalid_selection", error.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task Step_service_aggregates_reasoning_tail_for_activity_detail()
    {
        var provider = new FakeModelProvider(
            new ModelDelta(null, Array.Empty<ModelToolFragment>(), false, null, null, "first thought. "),
            new ModelDelta("done", Array.Empty<ModelToolFragment>(), true, "stop", null, "final thought."));
        var result = await new ModelStepService(provider).RunAsync(Request(), CancellationToken.None);

        Assert.Equal("done", result.Text);
        Assert.Equal("first thought. final thought.", result.ReasoningText);
    }

    [Fact]
    public async Task Step_service_falls_back_to_think_blocks_without_reasoning_channel()
    {
        var provider = new FakeModelProvider(
            new ModelDelta("answer <think>inner reasoning</think> tail", Array.Empty<ModelToolFragment>(), true, "stop"));
        var result = await new ModelStepService(provider).RunAsync(Request(), CancellationToken.None);

        Assert.Equal("inner reasoning", result.ReasoningText);
    }

    [Fact]
    public async Task Step_service_rejects_lone_xml_dialect_tool_call()
    {
        const string xmlAttempt =
            "<tool_call>\n<read>\n<args>\n<op>list</op>\n<path>D:\\\\Test Agent</path>\n</args>\n</read>";
        var provider = new FakeModelProvider(
            new ModelDelta(xmlAttempt, Array.Empty<ModelToolFragment>(), true, "stop"));

        var error = await Assert.ThrowsAsync<ModelStepException>(() =>
            new ModelStepService(provider).RunAsync(Request(includeTool: true), CancellationToken.None));

        Assert.Equal("invalid_selection", error.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task Step_service_keeps_mid_text_syntax_discussion_visible()
    {
        const string text = "To call a tool, write <tool_call> and then describe args. Now let me read the file list.";
        var provider = new FakeModelProvider(
            new ModelDelta(text, Array.Empty<ModelToolFragment>(), true, "stop"));
        var deltas = new List<string>();

        var result = await new ModelStepService(provider).RunAsync(
            Request(includeTool: true),
            text =>
            {
                deltas.Add(text);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(text, result.Text);
        Assert.Empty(result.ToolCalls);
        Assert.Equal(text, string.Concat(deltas));
    }

    [Fact]
    public async Task Step_service_keeps_ordinary_prose_visible_when_tools_are_advertised()
    {
        var provider = new FakeModelProvider(
            new ModelDelta("Đang kiểm tra cho anh.", Array.Empty<ModelToolFragment>(), true, "stop"));

        var result = await new ModelStepService(provider).RunAsync(Request(includeTool: true), CancellationToken.None);

        Assert.Equal("Đang kiểm tra cho anh.", result.Text);
        Assert.Empty(result.ToolCalls);
        Assert.Equal("stop", result.FinishReason);
    }

    [Fact]
    public async Task Malformed_native_history_fails_closed_before_dispatch()
    {
        // A native call batch without its results is deterministic host-side input:
        // it must fail closed before dispatch instead of spending the bounded retry
        // budget. (This once covered the text-selection projection of an unadvertised
        // tool, which retired with that calling mode.)
        var provider = new FakeModelProvider(new ModelDelta("must not run", [], true, "stop"));
        var request = Request(
            includeTool: true,
            bootstrap: Bootstrap("anthropic_messages", maximumOutputTokens: 8192),
            modelMessages: NativeMessagesWithUnknownTool());

        var error = await Assert.ThrowsAsync<ModelStepException>(() =>
            new ModelStepService(provider).RunAsync(request, CancellationToken.None));

        Assert.Equal("model_history_invalid", error.Code);
        Assert.False(error.Retryable);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Step_service_observes_cancellation_and_does_not_release_a_running_provider_early()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new BlockingProvider();
        var task = new ModelStepService(provider).RunAsync(Request(), cancellation.Token);
        await provider.Started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.True(provider.Cancelled.Task.IsCompleted);
    }

    [Fact]
    public async Task Step_service_observes_a_pre_cancelled_token_without_dispatching()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = new FakeModelProvider(new ModelDelta("must not run", [], true, "stop"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ModelStepService(provider).RunAsync(Request(), cancellation.Token));

        Assert.Empty(provider.Requests);
    }

    [Theory]
    [InlineData("chat_completions")]
    [InlineData("openai_responses")]
    [InlineData("anthropic_messages")]
    public async Task Step_service_keeps_native_messages_for_every_protocol(string protocol)
    {
        var provider = new FakeModelProvider(new ModelDelta("ok", [], true, "stop"));
        var request = Request(
            includeTool: true,
            bootstrap: Bootstrap(protocol, maximumOutputTokens: protocol == "anthropic_messages" ? 8192 : null),
            modelMessages: NativeMessages());

        var result = await new ModelStepService(provider).RunAsync(request, CancellationToken.None);

        Assert.Equal("ok", result.Text);
        var modelRequest = Assert.Single(provider.Requests);
        Assert.Equal("call_1", modelRequest.Turns[0].ToolCalls.Single().Id);
        Assert.Equal("call_1", modelRequest.Turns[1].ToolCallId);
        Assert.Equal("ok", modelRequest.Turns[1].ToolResultCode);
    }

    [Fact]
    public async Task Step_service_uses_the_per_step_protected_provider_snapshot()
    {
        var provider = new FakeModelProvider(new ModelDelta("ok", [], true, "stop"));
        var bootstrap = new ProviderBootstrapData(
            "Kilo",
            "chat_completions",
            new Uri("https://step-provider.invalid/v1"),
            "model-b",
            "step-secret",
            new JsonObject
            {
                ["reasoning"] = new JsonObject
                {
                    ["effort"] = "high",
                    ["enabled"] = true,
                },
            });

        await new ModelStepService(provider).RunAsync(
            Request(bootstrap: bootstrap),
            CancellationToken.None);

        var request = Assert.Single(provider.Requests);
        Assert.Equal("model-b", request.Model);
        Assert.Equal(new Uri("https://step-provider.invalid/v1"), request.Endpoint);
        Assert.Equal("step-secret", request.Credential);
        Assert.Equal("high", request.Settings["reasoning"]!["effort"]!.GetValue<string>());
        Assert.True(request.Settings["reasoning"]!["enabled"]!.GetValue<bool>());
        Assert.False(request.Settings.ContainsKey("reasoning_model"));
    }

    [Fact]
    public async Task Step_service_preserves_provider_default_omission()
    {
        var provider = new FakeModelProvider(new ModelDelta("ok", [], true, "stop"));

        await new ModelStepService(provider).RunAsync(
            Request(settings: new ModelGenerationSettings(maxOutputTokens: 256)),
            CancellationToken.None);

        var request = Assert.Single(provider.Requests);
        Assert.False(request.Settings.ContainsKey("reasoning_effort"));
        Assert.False(request.Settings.ContainsKey("max_tokens"));
        Assert.Null(request.MaximumOutputTokens);
    }

    [Fact]
    public async Task Provider_auth_failure_maps_to_a_bounded_error_without_credentials()
    {
        // Ported from the worker-wire integration test that was deleted with the
        // transport: the mapping it covered is the living provider->step contract.
        const string privatePrompt = "integration prompt must stay private";
        const string providerCredential = "integration-provider-secret";
        var provider = new ThrowingProvider(
            new ModelProviderException("auth_failed", retryable: false));

        var error = await Assert.ThrowsAsync<ModelStepException>(() =>
            new ModelStepService(provider).RunAsync(
                Request(
                    prompt: privatePrompt,
                    bootstrap: new ProviderBootstrapData(
                        "Test",
                        ProviderProtocols.ChatCompletions,
                        new Uri("https://step-provider.invalid/v1"),
                        "model-a",
                        providerCredential)),
                CancellationToken.None));

        Assert.Equal("provider_auth", error.Code);
        Assert.False(error.Retryable);
        Assert.DoesNotContain(providerCredential, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(privatePrompt, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http_error", false)]
    [InlineData("http_error", true)]
    [InlineData("vision_unsupported", false)]
    [InlineData("empty_model_reply", false)]
    public async Task Provider_codes_reach_the_typed_step_unchanged(string code, bool retryable)
    {
        // Ported from the worker-wire integration test that was deleted with the
        // transport: these codes pass through with their own name and retry flag.
        const string privatePrompt = "integration prompt must stay private";
        var provider = new ThrowingProvider(new ModelProviderException(code, retryable));

        var error = await Assert.ThrowsAsync<ModelStepException>(() =>
            new ModelStepService(provider).RunAsync(
                Request(prompt: privatePrompt),
                CancellationToken.None));

        Assert.Equal(code, error.Code);
        Assert.Equal(retryable, error.Retryable);
        Assert.DoesNotContain(privatePrompt, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("timeout", false, "provider_timeout", true)]
    [InlineData("redirect_rejected", false, "redirect_rejected", false)]
    [InlineData("auth_failed", false, "provider_auth", false)]
    [InlineData("forbidden", false, "provider_auth", false)]
    [InlineData("rate_limited", false, "provider_rate_limited", true)]
    [InlineData("quota_exhausted", true, "provider_quota_exhausted", false)]
    [InlineData("server_error", false, "provider_server_error", true)]
    [InlineData("transport_error", false, "provider_transport_error", true)]
    [InlineData("malformed_payload", false, "protocol_failure", true)]
    [InlineData("response_too_large", false, "protocol_failure", false)]
    [InlineData("unmapped_provider_code", false, "generation_failed", false)]
    [InlineData("unmapped_provider_code", true, "generation_failed", true)]
    public async Task Provider_failure_table_maps_code_and_retryability_exactly(
        string providerCode,
        bool providerRetryable,
        string expectedCode,
        bool expectedRetryable)
    {
        // Pins the whole MapProviderException table: renames or a flipped retry
        // flag reach the Host's durable code, so they must not go unnoticed.
        var provider = new ThrowingProvider(new ModelProviderException(providerCode, providerRetryable,
            retryAfter: TimeSpan.FromSeconds(17)));

        var error = await Assert.ThrowsAsync<ModelStepException>(() =>
            new ModelStepService(provider).RunAsync(Request(), CancellationToken.None));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(expectedRetryable, error.Retryable);
        if (providerCode is "rate_limited" or "server_error")
            Assert.Equal(TimeSpan.FromSeconds(17), error.RetryAfter);
    }

    [Fact]
    public void Typed_request_rejects_a_model_history_over_the_message_cap()
    {
        var messages = Enumerable.Range(0, ModelGenerationBounds.MaxModelMessages + 1)
            .Select(index => new ModelTurn("user", $"message {index}"))
            .ToArray();

        var error = Assert.Throws<ArgumentException>(() => new ModelGenerationRequest(
            "hello",
            messages,
            SystemPromptContext.Empty,
            new ModelGenerationSettings(),
            DefaultBootstrap));

        Assert.Equal("modelMessages", error.ParamName);
    }

    [Fact]
    public void Typed_request_rejects_a_prompt_over_the_prompt_cap()
    {
        var error = Assert.Throws<ArgumentException>(() => new ModelGenerationRequest(
            new string('a', ModelGenerationBounds.MaxPromptChars + 1),
            Array.Empty<ModelTurn>(),
            SystemPromptContext.Empty,
            new ModelGenerationSettings(),
            DefaultBootstrap));

        Assert.Equal("prompt", error.ParamName);
    }

    [Fact]
    public void Typed_request_requires_a_prompt_unless_an_image_is_attached()
    {
        var error = Assert.Throws<ArgumentException>(() => new ModelGenerationRequest(
            "   ",
            Array.Empty<ModelTurn>(),
            SystemPromptContext.Empty,
            new ModelGenerationSettings(),
            DefaultBootstrap));

        Assert.Equal("prompt", error.ParamName);

        var withImage = new ModelGenerationRequest(
            string.Empty,
            Array.Empty<ModelTurn>(),
            SystemPromptContext.Empty,
            new ModelGenerationSettings(),
            DefaultBootstrap,
            [new ModelImage("image/png", "aGVsbG8=")]);
        Assert.Empty(withImage.Prompt);
    }

    [Fact]
    public void Typed_settings_reject_an_out_of_range_output_cap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelGenerationSettings(maxOutputTokens: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelGenerationSettings(maxOutputTokens: 1_000_001));
    }

    [Fact]
    public async Task The_provider_step_bound_actually_lets_that_many_steps_overlap()
    {
        // The bound is what makes the runtime loop's four-run concurrency real for
        // model steps. Nothing measured that before, so a regression back to (1,1)
        // would pass the whole suite silently.
        const int concurrentSteps = 3;
        var provider = new ConcurrencyProbeProvider();
        var service = new ModelStepService(provider, concurrentSteps);
        var request = Request(bootstrap: Bootstrap(ProviderProtocols.ChatCompletions));

        var runs = Enumerable.Range(0, concurrentSteps)
            .Select(_ => service.RunAsync(request, CancellationToken.None))
            .ToArray();
        await Task.WhenAll(runs);

        Assert.Equal(concurrentSteps, provider.PeakConcurrency);
    }

    [Fact]
    public async Task A_bound_of_one_serializes_provider_steps()
    {
        var provider = new ConcurrencyProbeProvider();
        var service = new ModelStepService(provider, 1);
        var request = Request(bootstrap: Bootstrap(ProviderProtocols.ChatCompletions));

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => service.RunAsync(request, CancellationToken.None)));

        Assert.Equal(1, provider.PeakConcurrency);
    }

    [Fact]
    public void The_bound_variable_falls_back_to_the_default_when_it_is_unusable()
    {
        Assert.Equal(ModelStepService.DefaultMaxConcurrentProviderSteps,
            ModelStepService.ResolveMaxConcurrentProviderSteps(null));
        Assert.Equal(ModelStepService.DefaultMaxConcurrentProviderSteps,
            ModelStepService.ResolveMaxConcurrentProviderSteps(""));
        Assert.Equal(ModelStepService.DefaultMaxConcurrentProviderSteps,
            ModelStepService.ResolveMaxConcurrentProviderSteps("   "));
        Assert.Equal(ModelStepService.DefaultMaxConcurrentProviderSteps,
            ModelStepService.ResolveMaxConcurrentProviderSteps("not-a-number"));
        Assert.Equal(ModelStepService.DefaultMaxConcurrentProviderSteps,
            ModelStepService.ResolveMaxConcurrentProviderSteps("0"));
        Assert.Equal(ModelStepService.DefaultMaxConcurrentProviderSteps,
            ModelStepService.ResolveMaxConcurrentProviderSteps("-4"));

        Assert.Equal(1, ModelStepService.ResolveMaxConcurrentProviderSteps("1"));
        Assert.Equal(7, ModelStepService.ResolveMaxConcurrentProviderSteps("7"));
    }

    [Fact]
    public void The_constructor_rejects_a_bound_below_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ModelStepService(new ConcurrencyProbeProvider(), 0));
    }

    private static readonly ProviderBootstrapData DefaultBootstrap = new(
        "test-worker",
        ProviderProtocols.ChatCompletions,
        new Uri("http://127.0.0.1"),
        "test-worker",
        string.Empty,
        acceptsImageInput: true);

    private static ProviderBootstrapData Bootstrap(string protocol, int? maximumOutputTokens = null) => new(
        "test-worker",
        protocol,
        new Uri("http://127.0.0.1"),
        "test-worker",
        string.Empty,
        maximumOutputTokens: maximumOutputTokens);

    private static ModelGenerationRequest Request(
        bool includeTool = false,
        string toolId = "files.read",
        string prompt = "xin chào",
        ModelGenerationSettings? settings = null,
        ProviderBootstrapData? bootstrap = null,
        IReadOnlyList<ModelImage>? images = null,
        IReadOnlyList<ModelTurn>? modelMessages = null,
        SessionId? sessionId = null) =>
        new(
            prompt,
            modelMessages ?? Array.Empty<ModelTurn>(),
            SystemPromptContext.Empty,
            settings ?? new ModelGenerationSettings(includeTool ? ModelTools(toolId) : null),
            bootstrap ?? DefaultBootstrap,
            images,
            sessionId: sessionId);

    private static JsonArray ModelTools(string toolId) => new()
    {
        new JsonObject
        {
            ["tool_id"] = toolId,
            ["tool_version"] = "1.0.0",
            ["scopes"] = new JsonArray("workspace.read"),
            ["availability"] = "executable",
            ["model_enabled"] = true,
            ["capability_class"] = "tool",
        },
    };

    private static IReadOnlyList<ModelTurn> NativeMessages() =>
    [
        new ModelTurn(
            "assistant",
            "I will inspect.",
            [new ModelFunctionCall("call_1", "files.read", Json("{\"path\":\"a.txt\"}"))]),
        new ModelTurn("tool", "untrusted result", toolCallId: "call_1", toolResultCode: "ok"),
    ];

    private static IReadOnlyList<ModelTurn> NativeMessagesWithUnknownTool() =>
    [
        new ModelTurn(
            "assistant",
            "I will inspect.",
            [new ModelFunctionCall("call_1", "files.teleport", Json("{}"))]),
    ];

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private sealed class ProjectingFakeProvider : IModelProvider, IModelRequestBodyProjector
    {
        public string? DispatchedBodySha256 { get; private set; }
        public int DispatchedBodyBytes { get; private set; }

        public string ProjectRequestBody(ModelRequest request)
        {
            var body = new JsonObject
            {
                ["model"] = request.Model,
                ["system"] = request.SystemPrompt,
                ["prompt"] = request.Prompt,
                ["max_tokens"] = request.MaximumOutputTokens,
                ["turns"] = new JsonArray(request.Turns.Select(turn =>
                    (JsonNode?)JsonValue.Create(turn.Role + ":" + turn.Content)).ToArray()),
                ["images"] = new JsonArray(request.Images.Select(image =>
                    JsonValue.Create($"data:{image.MimeType};base64,{image.Base64Data}")).ToArray()),
            };
            return body.ToJsonString();
        }

        public async IAsyncEnumerable<ModelDelta> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var body = request.PreparedBodyJson
                ?? throw new InvalidOperationException("prepared body missing");
            var bytes = Encoding.UTF8.GetBytes(body);
            DispatchedBodyBytes = bytes.Length;
            DispatchedBodySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                .ToLowerInvariant();
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ModelDelta("prepared", [], true, "stop");
        }
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? BodySha256 { get; private set; }
        public int BodyBytes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var bytes = Encoding.UTF8.GetBytes(Body);
            BodyBytes = bytes.Length;
            BodySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class FakeModelProvider : IModelProvider
    {
        private readonly IReadOnlyList<ModelDelta> _deltas;
        public List<ModelRequest> Requests { get; } = new();

        public FakeModelProvider(params ModelDelta[] deltas) => _deltas = deltas;

        public async IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            foreach (var delta in _deltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return delta;
            }
        }
    }

    private sealed class ThrowingProvider : IModelProvider
    {
        private readonly ModelProviderException _error;

        public ThrowingProvider(ModelProviderException error) => _error = error;

        public IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request, CancellationToken cancellationToken) =>
            throw _error;
    }

    /// <summary>Reports how many generations were in flight simultaneously.</summary>
    private sealed class ConcurrencyProbeProvider : IModelProvider
    {
        private int _active;
        private int _peak;

        public int PeakConcurrency => Volatile.Read(ref _peak);

        public async IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var observed = Volatile.Read(ref _peak);
                if (active <= observed
                    || Interlocked.CompareExchange(ref _peak, active, observed) == observed)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }

            yield return new ModelDelta(
                "done",
                Array.Empty<ModelToolFragment>(),
                completed: true,
                finishReason: "stop");
        }
    }

    private sealed class BlockingProvider : IModelProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.SetResult();
                throw;
            }

            yield break;
        }
    }
}
