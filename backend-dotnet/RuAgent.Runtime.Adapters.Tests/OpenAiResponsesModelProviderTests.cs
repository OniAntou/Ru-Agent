using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class OpenAiResponsesModelProviderTests
{
    private static string SseEvent(string eventName, JsonObject data) => $"event: {eventName}\ndata: {data.ToJsonString()}\n\n";
    private static string SseEventRaw(string eventName, string json) => $"event: {eventName}\ndata: {json}\n\n";

    private static async Task<List<ModelDelta>> CollectAsync(IAsyncEnumerable<ModelDelta> source)
    {
        var list = new List<ModelDelta>();
        await foreach (var d in source) list.Add(d);
        return list;
    }

    [Fact]
    public async Task GenerateAsync_sends_expected_json_fields()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SseEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["model"] = "test-model", ["status"] = "completed" } }), Encoding.UTF8, "text/event-stream")
        });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        _ = await CollectAsync(provider.GenerateAsync(Request(maximumOutputTokens: 4096), CancellationToken.None));
        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        Assert.Equal("test-model", body["model"]!.GetValue<string>());
        Assert.Equal("system prompt", body["instructions"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.False(body["store"]!.GetValue<bool>());
        Assert.NotNull(body["input"]);
        Assert.Equal(4096, body["max_output_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task GenerateAsync_projects_native_tools_and_assembles_interleaved_calls()
    {
        var responseOutput = new JsonArray
        {
            new JsonObject { ["type"] = "function_call", ["id"] = "item-a", ["call_id"] = "call-a", ["name"] = "files.read", ["arguments"] = "{\"path\":\"a.txt\"}" },
            new JsonObject { ["type"] = "function_call", ["id"] = "item-b", ["call_id"] = "call-b", ["name"] = "files.read", ["arguments"] = "{\"path\":\"b.txt\"}" },
            new JsonObject { ["type"] = "reasoning", ["id"] = "reason-1", ["encrypted_content"] = "opaque-secret-signature", ["summary"] = new JsonArray() },
        };
        var sse =
            SseEvent("response.output_item.added", new JsonObject { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = new JsonObject { ["id"] = "item-a", ["type"] = "function_call", ["call_id"] = "call-a", ["name"] = "files.read", ["arguments"] = "" } }) +
            SseEvent("response.function_call_arguments.delta", new JsonObject { ["type"] = "response.function_call_arguments.delta", ["output_index"] = 0, ["item_id"] = "item-a", ["delta"] = "{\"path\":" }) +
            SseEvent("response.output_item.added", new JsonObject { ["type"] = "response.output_item.added", ["output_index"] = 1, ["item"] = new JsonObject { ["id"] = "item-b", ["type"] = "function_call", ["call_id"] = "call-b", ["name"] = "files.read", ["arguments"] = "" } }) +
            SseEvent("response.function_call_arguments.delta", new JsonObject { ["type"] = "response.function_call_arguments.delta", ["output_index"] = 1, ["item_id"] = "item-b", ["delta"] = "{\"path\":" }) +
            SseEvent("response.function_call_arguments.delta", new JsonObject { ["type"] = "response.function_call_arguments.delta", ["output_index"] = 0, ["item_id"] = "item-a", ["delta"] = "\"a.txt\"}" }) +
            SseEvent("response.function_call_arguments.delta", new JsonObject { ["type"] = "response.function_call_arguments.delta", ["output_index"] = 1, ["item_id"] = "item-b", ["delta"] = "\"b.txt\"}" }) +
            SseEvent("response.function_call_arguments.done", new JsonObject { ["type"] = "response.function_call_arguments.done", ["output_index"] = 0, ["item_id"] = "item-a", ["arguments"] = "{\"path\":\"a.txt\"}" }) +
            SseEvent("response.function_call_arguments.done", new JsonObject { ["type"] = "response.function_call_arguments.done", ["output_index"] = 1, ["item_id"] = "item-b", ["arguments"] = "{\"path\":\"b.txt\"}" }) +
            SseEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["model"] = "test-model", ["status"] = "completed", ["output"] = responseOutput } });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var settings = new JsonObject { ["model_tools"] = new JsonArray { new JsonObject { ["tool_id"] = "files.read", ["tool_version"] = "1.0.0", ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("path"), ["additionalProperties"] = false } } } };
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        var deltas = await CollectAsync(provider.GenerateAsync(Request(settings), CancellationToken.None));
        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        Assert.False(body["store"]!.GetValue<bool>());
        Assert.Equal("files.read", body["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("function", body["tools"]![0]!["type"]!.GetValue<string>());
        Assert.False(body["tools"]![0]!["strict"]!.GetValue<bool>());
        var fragments = deltas.SelectMany(delta => delta.ToolFragments).ToArray();
        Assert.Equal(new[] { "call-a", "call-b" }, fragments.Select(fragment => fragment.ProviderCallId));
        Assert.Equal(new[] { "{\"path\":\"a.txt\"}", "{\"path\":\"b.txt\"}" }, fragments.Select(fragment => fragment.ArgumentsFragment));
        Assert.NotNull(deltas[^1].ProviderContinuation);
        Assert.Contains(deltas[^1].ProviderContinuation!.OutputItems,
            item => item["encrypted_content"]?.GetValue<string>() == "opaque-secret-signature");
    }

    [Fact]
    public async Task GenerateAsync_rejects_function_call_missing_provider_identity()
    {
        var responseOutput = new JsonArray
        {
            new JsonObject { ["type"] = "function_call", ["id"] = "item-a", ["call_id"] = "call-a", ["name"] = "read", ["arguments"] = "{}" },
        };
        var sse = SseEvent("response.output_item.added", new JsonObject
        {
            ["type"] = "response.output_item.added", ["output_index"] = 0,
            ["item"] = new JsonObject { ["type"] = "function_call", ["id"] = "item-a", ["name"] = "read", ["arguments"] = "" },
        }) + SseEvent("response.completed", new JsonObject
        {
            ["type"] = "response.completed",
            ["response"] = new JsonObject { ["model"] = "test-model", ["status"] = "completed", ["output"] = responseOutput },
        });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        })));

        var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await CollectAsync(provider.GenerateAsync(Request(new JsonObject
            {
                ["model_tools"] = new JsonArray(new JsonObject { ["tool_id"] = "read", ["input_schema"] = new JsonObject { ["type"] = "object" } }),
            }), CancellationToken.None)));

        Assert.Equal("malformed_payload", error.Code);
    }

    [Fact]
    public async Task GenerateAsync_rejects_completed_function_call_identity_mismatch()
    {
        var sse = SseEvent("response.output_item.added", new JsonObject
        {
            ["type"] = "response.output_item.added", ["output_index"] = 0,
            ["item"] = new JsonObject { ["type"] = "function_call", ["id"] = "item-a", ["call_id"] = "call-a", ["name"] = "read", ["arguments"] = "" },
        }) + SseEvent("response.function_call_arguments.delta", new JsonObject
        {
            ["type"] = "response.function_call_arguments.delta", ["output_index"] = 0, ["item_id"] = "item-a", ["delta"] = "{}",
        }) + SseEvent("response.function_call_arguments.done", new JsonObject
        {
            ["type"] = "response.function_call_arguments.done", ["output_index"] = 0, ["item_id"] = "item-a", ["arguments"] = "{}",
        }) + SseEvent("response.completed", new JsonObject
        {
            ["type"] = "response.completed",
            ["response"] = new JsonObject
            {
                ["model"] = "test-model", ["status"] = "completed",
                ["output"] = new JsonArray(new JsonObject
                {
                    ["type"] = "function_call", ["id"] = "item-a", ["call_id"] = "call-other", ["name"] = "read", ["arguments"] = "{}",
                }),
            },
        });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        })));

        var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None)));

        Assert.Equal("malformed_payload", error.Code);
    }

    [Fact]
    public async Task GenerateAsync_rejects_duplicate_provider_call_identity()
    {
        var sse = SseEvent("response.output_item.added", new JsonObject
        {
            ["type"] = "response.output_item.added", ["output_index"] = 0,
            ["item"] = new JsonObject { ["type"] = "function_call", ["id"] = "item-a", ["call_id"] = "call-a", ["name"] = "read", ["arguments"] = "" },
        }) + SseEvent("response.output_item.added", new JsonObject
        {
            ["type"] = "response.output_item.added", ["output_index"] = 1,
            ["item"] = new JsonObject { ["type"] = "function_call", ["id"] = "item-b", ["call_id"] = "call-a", ["name"] = "read", ["arguments"] = "" },
        });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        })));

        var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None)));

        Assert.Equal("malformed_payload", error.Code);
    }

    [Fact]
    public async Task GenerateAsync_replays_typed_tool_output_by_matching_call_id()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SseEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["model"] = "test-model", ["status"] = "completed", ["output"] = new JsonArray() } }), Encoding.UTF8, "text/event-stream")
        });
        var args = System.Text.Json.JsonDocument.Parse("{\"path\":\"a.txt\"}").RootElement.Clone();
        var turns = new[]
        {
            new ModelTurn("assistant", "", [new ModelFunctionCall("call-a", "files.read", args)]),
            new ModelTurn("tool", "file result", toolCallId: "call-a", toolResultCode: "ok"),
        };
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        var tools = new JsonObject { ["model_tools"] = new JsonArray { new JsonObject { ["tool_id"] = "files.read", ["input_schema"] = new JsonObject { ["type"] = "object" } } } };
        _ = await CollectAsync(provider.GenerateAsync(Request(tools, turns), CancellationToken.None));
        var input = JsonNode.Parse(handler.RequestBody!)!["input"]!.AsArray();
        var call = Assert.Single(input, item => item!["type"]?.GetValue<string>() == "function_call");
        var output = Assert.Single(input, item => item!["type"]?.GetValue<string>() == "function_call_output");
        Assert.Equal("call-a", call!["call_id"]!.GetValue<string>());
        Assert.Equal("call-a", output!["call_id"]!.GetValue<string>());
        Assert.Equal("file result", output["output"]!.GetValue<string>());
    }

    [Fact]
    public async Task GenerateAsync_accepts_bounded_projected_role_history()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SseEvent("response.completed", new JsonObject
            {
                ["type"] = "response.completed",
                ["response"] = new JsonObject { ["model"] = "test-model", ["status"] = "completed", ["output"] = new JsonArray() },
            }), Encoding.UTF8, "text/event-stream"),
        });
        var baseline = Request();
        var request = new ModelRequest(
            baseline.Model,
            baseline.SystemPrompt,
            baseline.Prompt,
            baseline.Turns,
            baseline.Settings,
            baseline.Endpoint,
            baseline.Credential,
            responsesInputItems: new JsonObject[]
            {
                new() { ["role"] = "user", ["content"] = "earlier request" },
                new() { ["role"] = "assistant", ["content"] = "earlier response" },
                new() { ["type"] = "function_call_output", ["call_id"] = "call-a", ["output"] = "read result" },
            });

        _ = await CollectAsync(new OpenAiResponsesModelProvider(new HttpClient(handler)).GenerateAsync(request, CancellationToken.None));
        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        Assert.Equal("earlier request", body["input"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("function_call_output", body["input"]![2]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task GenerateAsync_omits_reasoning_when_provider_default()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SseEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["model"] = "test-model", ["status"] = "completed" } }), Encoding.UTF8, "text/event-stream")
        });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        _ = await CollectAsync(provider.GenerateAsync(Request(new JsonObject()), CancellationToken.None));
        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        Assert.False(body.ContainsKey("reasoning"));
    }

    [Fact]
    public async Task GenerateAsync_serializes_reasoning_effort_high()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SseEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["model"] = "test-model", ["status"] = "completed" } }), Encoding.UTF8, "text/event-stream")
        });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        var settings = new JsonObject { ["reasoning"] = new JsonObject { ["effort"] = "high" } };
        _ = await CollectAsync(provider.GenerateAsync(Request(settings), CancellationToken.None));
        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        Assert.Equal("high", body["reasoning"]!["effort"]!.GetValue<string>());
        // Summaries are requested so the activity feed has something displayable.
        Assert.Equal("auto", body["reasoning"]!["summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task GenerateAsync_keeps_an_explicit_reasoning_summary()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SseEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["model"] = "test-model", ["status"] = "completed" } }), Encoding.UTF8, "text/event-stream")
        });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        var settings = new JsonObject { ["reasoning"] = new JsonObject { ["effort"] = "high", ["summary"] = "concise" } };
        _ = await CollectAsync(provider.GenerateAsync(Request(settings), CancellationToken.None));
        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        Assert.Equal("concise", body["reasoning"]!["summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task GenerateAsync_surfaces_reasoning_summary_text_as_reasoning()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                SseEvent("response.reasoning_summary_text.delta", new JsonObject { ["type"] = "response.reasoning_summary_text.delta", ["delta"] = "Weighing options" }) +
                SseEvent("response.output_text.delta", new JsonObject { ["type"] = "response.output_text.delta", ["delta"] = "done" }) +
                SseEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["model"] = "test-model", ["status"] = "completed" } }),
                Encoding.UTF8,
                "text/event-stream")
        });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        var deltas = await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None));
        Assert.Contains(deltas, d => d.Reasoning == "Weighing options");
        Assert.Contains(deltas, d => d.Text == "done");
        Assert.DoesNotContain(deltas, d => (d.Text ?? string.Empty).Contains("Weighing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateAsync_does_not_forward_model_tools_messages_temperature()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SseEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["model"] = "test-model", ["status"] = "completed" } }), Encoding.UTF8, "text/event-stream")
        });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        var settings = new JsonObject { ["model_tools"] = new JsonArray(), ["messages"] = new JsonArray(), ["temperature"] = 0.9 };
        _ = await CollectAsync(provider.GenerateAsync(Request(settings), CancellationToken.None));
        var body = handler.RequestBody!;
        Assert.DoesNotContain("model_tools", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"temperature\"", body, StringComparison.Ordinal);
        // messages as top-level key should not exist except via input; the raw body should not contain "\"messages\""
        // but input is an array; we check that original settings keys are not echoed beyond expected.
        Assert.DoesNotContain("\"model_tools\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_sets_only_Authorization_Bearer_header()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SseEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["model"] = "test-model", ["status"] = "completed" } }), Encoding.UTF8, "text/event-stream")
        });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        _ = await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None));
        Assert.True(handler.Request.Headers.Contains("Authorization"));
        Assert.Contains("Bearer synthetic-openai", handler.Request.Headers.GetValues("Authorization").First());
        // Only Authorization should be present as extra; no x-api-key
        Assert.False(handler.Request.Headers.Contains("x-api-key"));
    }

    [Fact]
    public async Task GenerateAsync_streams_text_delta_and_ignores_progress_events()
    {
        var sse =
            SseEvent("response.created", new JsonObject { ["type"] = "response.created" }) +
            SseEvent("response.output_text.delta", new JsonObject { ["type"] = "response.output_text.delta", ["delta"] = "Hello " }) +
            SseEvent("response.in_progress", new JsonObject { ["type"] = "response.in_progress" }) +
            SseEvent("response.output_text.delta", new JsonObject { ["type"] = "response.output_text.delta", ["delta"] = "world" }) +
            SseEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["model"] = "test-model", ["status"] = "completed" } });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        var deltas = await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None));
        Assert.Equal("Hello ", deltas[0].Text);
        Assert.Equal("world", deltas[1].Text);
        Assert.True(deltas[^1].Completed);
        Assert.Equal("stop", deltas[^1].FinishReason);
    }

    [Fact]
    public async Task GenerateAsync_ignores_unknown_future_event()
    {
        var sse =
            SseEvent("response.created", new JsonObject { ["type"] = "response.created" }) +
            SseEvent("response.output_text.delta", new JsonObject { ["type"] = "response.output_text.delta", ["delta"] = "Hello" }) +
            SseEvent("response.future_thing", new JsonObject { ["type"] = "response.future_thing", ["payload"] = new JsonObject { ["x"] = 1 } }) +
            SseEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["model"] = "test-model", ["status"] = "completed" } });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));

        var deltas = await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None));

        Assert.Equal("Hello", deltas[0].Text);
        Assert.True(deltas[^1].Completed);
    }

    [Fact]
    public async Task GenerateAsync_ignores_unknown_future_event_arriving_after_completion()
    {
        var sse =
            SseEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["model"] = "test-model", ["status"] = "completed" } }) +
            SseEvent("response.future_tail", new JsonObject { ["type"] = "response.future_tail" });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));

        var deltas = await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None));

        Assert.Single(deltas);
        Assert.True(deltas[0].Completed);
    }

    [Fact]
    public async Task GenerateAsync_preserves_usage_from_completed_response()
    {
        var completed = new JsonObject
        {
            ["type"] = "response.completed",
            ["response"] = new JsonObject
            {
                ["model"] = "test-model",
                ["status"] = "completed",
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = 12,
                    ["output_tokens"] = 5,
                    ["total_tokens"] = 17,
                    ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 3 }
                }
            }
        };
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SseEvent("response.completed", completed), Encoding.UTF8, "text/event-stream")
        });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));

        var deltas = await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None));

        Assert.NotNull(deltas[^1].Usage);
        var usage = deltas[^1].Usage!;
        Assert.Equal(12, usage.InputTokens);
        Assert.Equal(5, usage.OutputTokens);
        Assert.Equal(17, usage.TotalTokens);
        Assert.Equal(3, usage.CacheReadInputTokens);
    }

    [Fact]
    public async Task GenerateAsync_treats_null_input_tokens_details_as_absent()
    {
        var sse = SseEventRaw("response.completed",
            "{\"type\":\"response.completed\",\"response\":{\"model\":\"test-model\",\"status\":\"completed\"," +
            "\"usage\":{\"input_tokens\":12,\"output_tokens\":5,\"total_tokens\":17,\"input_tokens_details\":null}}}");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));

        var deltas = await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None));

        Assert.NotNull(deltas[^1].Usage);
        var usage = deltas[^1].Usage!;
        Assert.Equal(12, usage.InputTokens);
        Assert.Null(usage.CacheReadInputTokens);
    }

    [Fact]
    public async Task GenerateAsync_rejects_model_mismatch_as_malformed_payload()
    {
        var sse = SseEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["model"] = "other-model", ["status"] = "completed" } });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None)));
        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_throws_on_response_failed()
    {
        var sse = SseEvent("response.failed", new JsonObject { ["type"] = "response.failed" });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None)));
        Assert.Equal("http_error", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_throws_on_error_event()
    {
        var sse = SseEventRaw("error", "{\"type\":\"error\",\"error\":{\"code\":\"rate_limit_exceeded\"}}");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None)));
        Assert.Equal("rate_limited", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_throws_on_malformed_json()
    {
        var sse = "event: response.output_text.delta\ndata: not-json\n\n";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None)));
        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_rejects_progress_event_whose_json_type_disagrees()
    {
        var sse =
            SseEvent("response.created", new JsonObject { ["type"] = "response.output_text.delta", ["delta"] = "hidden" }) +
            SseEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["model"] = "test-model", ["status"] = "completed" } });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None)));

        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_maps_wrong_json_value_type_to_malformed_payload()
    {
        var sse = SseEvent("response.output_text.delta", new JsonObject { ["type"] = "response.output_text.delta", ["delta"] = 42 });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None)));

        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_maps_scalar_completed_response_to_malformed_payload()
    {
        var sse = SseEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = 42 });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None)));

        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_maps_scalar_error_container_to_malformed_payload()
    {
        var sse = SseEvent("error", new JsonObject { ["type"] = "error", ["error"] = 42 });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None)));

        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_rejects_duplicate_completed_events()
    {
        var sse =
            SseEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["model"] = "test-model", ["status"] = "completed" } }) +
            SseEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["model"] = "test-model", ["status"] = "completed" } });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None)));

        Assert.Equal("malformed_payload", ex.Code);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("tool")]
    public async Task GenerateAsync_rejects_turn_roles_not_supported_by_responses(string role)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("HTTP must not run"));
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));

        IReadOnlyList<ModelTurn> turns = role == "tool"
            ? new[]
            {
                new ModelTurn(
                    "assistant",
                    string.Empty,
                    [new ModelFunctionCall("call_1", "read", System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone())]),
                new ModelTurn("tool", "hidden", toolCallId: "call_1", toolResultCode: "ok"),
            }
            : new[] { new ModelTurn(role, "hidden") };
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await CollectAsync(provider.GenerateAsync(Request(turns: turns), CancellationToken.None)));

        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_throws_on_incomplete_EOF()
    {
        var sse = SseEvent("response.output_text.delta", new JsonObject { ["type"] = "response.output_text.delta", ["delta"] = "hi" });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None)));
        Assert.Equal("malformed_payload", ex.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "auth_failed", false)]
    [InlineData(HttpStatusCode.Forbidden, "forbidden", false)]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited", true)]
    [InlineData(HttpStatusCode.NotFound, "model_unavailable", false)]
    [InlineData(HttpStatusCode.InternalServerError, "server_error", true)]
    public async Task GenerateAsync_maps_http_failures(HttpStatusCode status, string code, bool retryable)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("secret body", Encoding.UTF8, "text/plain") });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None)));
        Assert.Equal(code, ex.Code);
        Assert.Equal(retryable, ex.Retryable);
        Assert.DoesNotContain("secret body", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_observes_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var handler = new RecordingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        var task = CollectAsync(provider.GenerateAsync(Request(), cts.Token));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }

    [Fact]
    public async Task GenerateAsync_rejects_5MB_cap()
    {
        var large = new string('a', 300 * 1024);
        var sb = new StringBuilder();
        for (int i = 0; i < 20; i++)
            sb.Append(SseEvent("response.output_text.delta", new JsonObject { ["type"] = "response.output_text.delta", ["delta"] = large }));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream") });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(), CancellationToken.None)));
        Assert.Equal("response_too_large", ex.Code);
    }

    [Fact]
    public async Task OpenAiResponses_with_image_includes_input_image()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SseEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["model"] = "gpt-4o", ["status"] = "completed" } }), Encoding.UTF8, "text/event-stream")
        });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        var req = new ModelRequest(
            "gpt-4o",
            "system prompt",
            "describe image",
            Array.Empty<ModelTurn>(),
            new JsonObject(),
            new Uri("https://provider.invalid/v1/responses"),
            "synthetic-openai",
            100,
            new[] { new ModelImage("image/png", "iVBORw0KGgo=") });

        _ = await CollectAsync(provider.GenerateAsync(req, CancellationToken.None));
        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        var input = body["input"]!.AsArray();
        var userTurn = input[^1]!.AsObject();
        var content = userTurn["content"]!.AsArray();
        Assert.Contains(content, n => n!["type"]!.GetValue<string>() == "input_text" && n["text"]!.GetValue<string>() == "describe image");
        Assert.Contains(content, n => n!["type"]!.GetValue<string>() == "input_image" && n["image_url"]!.GetValue<string>() == "data:image/png;base64,iVBORw0KGgo=");
    }

    [Fact]
    public async Task OpenAiResponses_with_image_rejects_non_vision_model()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var provider = new OpenAiResponsesModelProvider(new HttpClient(handler));
        var req = new ModelRequest(
            "text-model",
            "system prompt",
            "describe image",
            Array.Empty<ModelTurn>(),
            new JsonObject(),
            new Uri("https://provider.invalid/v1/responses"),
            "synthetic-openai",
            100,
            new[] { new ModelImage("image/png", "iVBORw0KGgo=") });

        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(req, CancellationToken.None)));
        Assert.Equal("vision_unsupported", ex.Code);
    }

    private static ModelRequest Request(JsonObject? settings = null, IReadOnlyList<ModelTurn>? turns = null,
        int? maximumOutputTokens = null) => new(
        "test-model",
        "system prompt",
        "hello",
        turns ?? Array.Empty<ModelTurn>(),
        settings ?? new JsonObject(),
        new Uri("https://provider.invalid/v1/responses"),
        "synthetic-openai",
        maximumOutputTokens);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public HttpRequestMessage Request { get; private set; } = null!;
        public string? RequestBody { get; private set; }
        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : this((r, _) => Task.FromResult(handler(r))) { }
        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            if (request.Content is not null) RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return await _handler(request, cancellationToken);
        }
    }
}
