using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Worker;
using RuBackend.Harness.Worker;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Worker;

public sealed class WorkerV2ClientTests : IAsyncDisposable
{
    private WebApplication? _app;
    private string _url = string.Empty;
    private string _token = "test-token";

    private async Task<string> StartServer(Func<HttpContext, Task> handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/worker/v2", handler);
        await app.StartAsync();
        _app = app;
        var address = app.Urls.First();
        _url = address.Replace("http://", "ws://").TrimEnd('/') + "/worker/v2";
        return _url;
    }

    [Fact]
    public async Task RunStepAsync_retries_the_worker_connect_within_the_startup_window()
    {
        var options = new AgentWorkerClientOptions(new Uri("ws://127.0.0.1:1/worker/v2"), _token);
        await using var runner = new LoopbackModelStepRunner(options)
        {
            StartupConnectWindowOverride = TimeSpan.FromMilliseconds(300),
        };
        var context = new ModelContextSnapshot(
            new[] { RuAgent.Harness.Core.Context.ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "test prompt") },
            Array.Empty<byte>());
        var request = new ModelStepRequest(
            new RunId("run_11111111111111111111111111111111"),
            new ModelStepId("step_11111111111111111111111111111111"),
            context,
            AgentPermissionPreset.Standard,
            1,
            new string('a', 64));

        var started = Environment.TickCount64;
        await Assert.ThrowsAsync<WorkerUnavailableException>(
            () => runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.True(
            Environment.TickCount64 - started >= 300,
            "the bounded startup retry window was not applied.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunStepAsync_maps_deltas_and_exactly_one_final_AssistantStep()
    {
        var tcs = new TaskCompletionSource();
        await StartServer(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64 * 1024];
            // Hello
            var hello = await ReceiveTextAsync(socket);
            var helloJson = JsonNode.Parse(hello) as JsonObject ?? throw new InvalidOperationException();
            Assert.Equal("hello", helloJson["type"]!.GetValue<string>());
            var helloId = helloJson["id"]!.GetValue<string>();
            var helloOk = new JsonObject
            {
                ["v"] = 2,
                ["id"] = helloId,
                ["type"] = "hello.ok",
                ["seq"] = 0,
                ["payload"] = new JsonObject { ["version"] = 2, ["capabilities"] = new JsonArray("agent.step") },
            };
            await SendTextAsync(socket, helloOk.ToJsonString());

            // Start
            var start = await ReceiveTextAsync(socket);
            var startJson = JsonNode.Parse(start) as JsonObject ?? throw new InvalidOperationException();
            Assert.Equal("agent.step.start", startJson["type"]!.GetValue<string>());
            var requestId = startJson["id"]!.GetValue<string>();
            var correlation = startJson["correlation_id"]!.GetValue<string>();

            // Delta
            var delta1 = new JsonObject
            {
                ["v"] = 2,
                ["id"] = requestId,
                ["type"] = "agent.step.delta",
                ["seq"] = 1,
                ["correlation_id"] = correlation,
                ["payload"] = new JsonObject { ["text"] = "Hello " },
            };
            await SendTextAsync(socket, delta1.ToJsonString());

            var delta2 = new JsonObject
            {
                ["v"] = 2,
                ["id"] = requestId,
                ["type"] = "agent.step.delta",
                ["seq"] = 2,
                ["correlation_id"] = correlation,
                ["payload"] = new JsonObject { ["text"] = "world" },
            };
            await SendTextAsync(socket, delta2.ToJsonString());

            // Done
            var done = new JsonObject
            {
                ["v"] = 2,
                ["id"] = requestId,
                ["type"] = "agent.step.done",
                ["seq"] = 3,
                ["correlation_id"] = correlation,
                ["payload"] = new JsonObject
                {
                    ["text"] = "Hello world",
                    ["tool_calls"] = new JsonArray(),
                    ["finish_reason"] = "stop",
                    ["prompt_receipt"] = PromptReceiptJson(),
                    ["usage"] = new JsonObject
                    {
                        ["input_tokens"] = 12,
                        ["output_tokens"] = 5,
                        ["total_tokens"] = 17
                    },
                },
            };
            await SendTextAsync(socket, done.ToJsonString());
            await Task.Delay(500);
            tcs.TrySetResult();
        });

        var options = new AgentWorkerClientOptions(new Uri(_url), _token);
        var runner = new LoopbackModelStepRunner(options);
        var context = new ModelContextSnapshot(
            new[] { RuAgent.Harness.Core.Context.ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "test prompt") },
            Array.Empty<byte>());
        var request = new ModelStepRequest(new RunId("run_11111111111111111111111111111111"), new ModelStepId("step_11111111111111111111111111111111"), context, AgentPermissionPreset.Standard, 1, new string('a', 64));
        var deltas = new List<string>();
        var step = await runner.RunStepAsync(request, delta => { deltas.Add(delta.Content); return ValueTask.CompletedTask; }, CancellationToken.None);
        Assert.Equal("Hello world", step.Step.Text);
        Assert.Empty(step.Step.ToolCalls);
        Assert.Equal(AssistantFinishReason.Stop, step.Step.FinishReason);
        Assert.Equal(12, step.Usage!.InputTokens);
        Assert.Equal(5, step.Usage.OutputTokens);
        Assert.Equal(17, step.Usage.TotalTokens);
        Assert.Equal(2, deltas.Count);
        Assert.Equal("Hello ", deltas[0]);
        Assert.Equal("world", deltas[1]);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RunStepAsync_maps_tool_calls_after_validating_prompt_receipt()
    {
        await StartServer(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var hello = JsonNode.Parse(await ReceiveTextAsync(socket))!.AsObject();
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = hello["id"]!.GetValue<string>(),
                ["type"] = "hello.ok",
                ["seq"] = 0,
                ["payload"] = new JsonObject
                {
                    ["version"] = 2,
                    ["capabilities"] = new JsonArray("agent.step"),
                },
            }.ToJsonString());

            var start = JsonNode.Parse(await ReceiveTextAsync(socket))!.AsObject();
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = start["id"]!.GetValue<string>(),
                ["type"] = "agent.step.done",
                ["seq"] = 1,
                ["correlation_id"] = start["correlation_id"]!.GetValue<string>(),
                ["payload"] = new JsonObject
                {
                    ["text"] = null,
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["provider_call_id"] = "call-1",
                        ["tool_id"] = "read",
                        ["tool_version"] = "1.0.0",
                        ["arguments"] = new JsonObject { ["op"] = "read", ["path"] = "README.md" },
                    }),
                    ["finish_reason"] = "tool_calls",
                    ["prompt_receipt"] = PromptReceiptJson(),
                },
            }.ToJsonString());
        });

        await using var runner = new LoopbackModelStepRunner(
            new AgentWorkerClientOptions(new Uri(_url), _token));
        var request = new ModelStepRequest(
            new RunId("run_44444444444444444444444444444444"),
            new ModelStepId("step_44444444444444444444444444444444"),
            new ModelContextSnapshot(
                [ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "read README")],
                Array.Empty<byte>()),
            AgentPermissionPreset.Standard,
            1,
            new string('f', 64));

        var result = await runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(AssistantFinishReason.ToolCalls, result.Step.FinishReason);
        var call = Assert.Single(result.Step.ToolCalls);
        Assert.Equal("call-1", call.ProviderCallId);
        Assert.Equal("read", call.ToolId);
        Assert.Equal("read", call.Arguments.GetProperty("op").GetString());
        Assert.Equal("README.md", call.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task RunStepAsync_sends_only_typed_prompt_context_and_derives_web_search_capability()
    {
        JsonObject? observedStart = null;
        var promptContext = PromptContextFixture.Create();
        await StartServer(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var hello = JsonNode.Parse(await ReceiveTextAsync(socket))!.AsObject();
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = hello["id"]!.GetValue<string>(),
                ["type"] = "hello.ok",
                ["seq"] = 0,
                ["payload"] = new JsonObject
                {
                    ["version"] = 2,
                    ["capabilities"] = new JsonArray("agent.step"),
                },
            }.ToJsonString());

            observedStart = JsonNode.Parse(await ReceiveTextAsync(socket))!.AsObject();
            var correlation = observedStart["correlation_id"]!.GetValue<string>();
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = observedStart["id"]!.GetValue<string>(),
                ["type"] = "agent.step.done",
                ["seq"] = 1,
                ["correlation_id"] = correlation,
                ["payload"] = new JsonObject
                {
                    ["text"] = "ok",
                    ["tool_calls"] = new JsonArray(),
                    ["finish_reason"] = "stop",
                    ["prompt_receipt"] = PromptReceiptJson(promptContext),
                },
            }.ToJsonString());
        });

        await using var runner = new LoopbackModelStepRunner(
            new AgentWorkerClientOptions(new Uri(_url), _token));
        var request = new ModelStepRequest(
            new RunId("run_11111111111111111111111111111111"),
            new ModelStepId("step_11111111111111111111111111111111"),
            new ModelContextSnapshot(
                [ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "prompt")],
                Array.Empty<byte>()),
            promptContext,
            AgentPermissionPreset.Standard,
            1,
            new string('d', 64));

        await runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None);

        var payload = observedStart!["payload"]!.AsObject();
        Assert.Equal(
            ["prompt", "turns", "system_prompt_context", "settings"],
            payload.Select(property => property.Key));
        Assert.False(payload.ContainsKey("memory_context"));
        Assert.False(payload["settings"]!.AsObject().ContainsKey("allow_web_search"));
        Assert.Equal(
            "untrusted tool result",
            payload["system_prompt_context"]!.AsObject()["runtime_context"]!.AsObject()["references"]!.AsArray()[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task RunStepAsync_creates_and_cleans_a_per_step_provider_handoff()
    {
        var root = Directory.CreateTempSubdirectory("ru-loopback-bootstrap-");
        JsonObject? observedStart = null;
        try
        {
            await StartServer(async context =>
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var hello = JsonNode.Parse(await ReceiveTextAsync(socket))!.AsObject();
                await SendTextAsync(socket, new JsonObject
                {
                    ["v"] = 2,
                    ["id"] = hello["id"]!.GetValue<string>(),
                    ["type"] = "hello.ok",
                    ["seq"] = 0,
                    ["payload"] = new JsonObject
                    {
                        ["version"] = 2,
                        ["capabilities"] = new JsonArray("agent.step"),
                    },
                }.ToJsonString());

                observedStart = JsonNode.Parse(await ReceiveTextAsync(socket))!.AsObject();
                var correlation = observedStart["correlation_id"]!.GetValue<string>();
                await SendTextAsync(socket, new JsonObject
                {
                    ["v"] = 2,
                    ["id"] = observedStart["id"]!.GetValue<string>(),
                    ["type"] = "agent.step.done",
                    ["seq"] = 1,
                    ["correlation_id"] = correlation,
                    ["payload"] = new JsonObject
                    {
                        ["text"] = "ok",
                        ["tool_calls"] = new JsonArray(),
                        ["finish_reason"] = "stop",
                        ["prompt_receipt"] = PromptReceiptJson(),
                    },
                }.ToJsonString());
            });

            await using var runner = new LoopbackModelStepRunner(
                new AgentWorkerClientOptions(new Uri(_url), _token))
            {
                CreateProviderBootstrapAsync = (stepRequest, _) =>
                {
                    Assert.Equal("run_33333333333333333333333333333333", stepRequest.RunId.Value);
                    return Task.FromResult(ProtectedProviderBootstrap.Write(
                    root.FullName,
                    new ProviderBootstrapData(
                        "Kilo",
                        "chat_completions",
                        new Uri("https://step-provider.invalid/v1"),
                        "model-b",
                        "step-secret",
                        new JsonObject
                        {
                            ["reasoning_effort"] = "high",
                        })));
                },
            };

            var request = new ModelStepRequest(
                new RunId("run_33333333333333333333333333333333"),
                new ModelStepId("step_33333333333333333333333333333333"),
                new ModelContextSnapshot(
                    [ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "prompt")],
                    Array.Empty<byte>()),
                AgentPermissionPreset.Standard,
                1,
                new string('f', 64));

            await runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None);

            var path = observedStart!["payload"]!["provider_bootstrap_path"]!.GetValue<string>();
            Assert.StartsWith(root.FullName, path, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(path));
            Assert.DoesNotContain("step-secret", observedStart.ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RunStepAsync_does_not_publish_deltas_before_receipt_validation()
    {
        await StartServer(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var hello = JsonNode.Parse(await ReceiveTextAsync(socket))!.AsObject();
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = hello["id"]!.GetValue<string>(),
                ["type"] = "hello.ok",
                ["seq"] = 0,
                ["payload"] = new JsonObject
                {
                    ["version"] = 2,
                    ["capabilities"] = new JsonArray("agent.step"),
                },
            }.ToJsonString());

            var start = JsonNode.Parse(await ReceiveTextAsync(socket))!.AsObject();
            var correlation = start["correlation_id"]!.GetValue<string>();
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = start["id"]!.GetValue<string>(),
                ["type"] = "agent.step.delta",
                ["seq"] = 1,
                ["correlation_id"] = correlation,
                ["payload"] = new JsonObject { ["text"] = "must stay buffered" },
            }.ToJsonString());
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = start["id"]!.GetValue<string>(),
                ["type"] = "agent.step.done",
                ["seq"] = 2,
                ["correlation_id"] = correlation,
                ["payload"] = new JsonObject
                {
                    ["text"] = "done",
                    ["tool_calls"] = new JsonArray(),
                    ["finish_reason"] = "stop",
                    ["prompt_receipt"] = new JsonObject
                    {
                        ["composer_version"] = "1.0.0",
                        ["prompt_sha256"] = new string('0', 64),
                        ["block_digests"] = new JsonArray(),
                    },
                },
            }.ToJsonString());
        });

        await using var runner = new LoopbackModelStepRunner(
            new AgentWorkerClientOptions(new Uri(_url), _token));
        var deltas = new List<string>();
        var request = new ModelStepRequest(
            new RunId("run_22222222222222222222222222222222"),
            new ModelStepId("step_22222222222222222222222222222222"),
            new ModelContextSnapshot(
                [ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "prompt")],
                Array.Empty<byte>()),
            AgentPermissionPreset.Standard,
            1,
            new string('e', 64));

        var error = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            runner.RunStepAsync(
                request,
                delta =>
                {
                    deltas.Add(delta.Content);
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None));

        Assert.Equal("invalid_step", error.Code);
        Assert.Empty(deltas);
    }

    [Fact]
    public async Task RunStepAsync_maps_malformed_usage_to_invalid_step()
    {
        await StartServer(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var hello = JsonNode.Parse(await ReceiveTextAsync(socket))!.AsObject();
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = hello["id"]!.GetValue<string>(),
                ["type"] = "hello.ok",
                ["seq"] = 0,
                ["payload"] = new JsonObject
                {
                    ["version"] = 2,
                    ["capabilities"] = new JsonArray("agent.step"),
                },
            }.ToJsonString());

            var start = JsonNode.Parse(await ReceiveTextAsync(socket))!.AsObject();
            var correlation = start["correlation_id"]!.GetValue<string>();
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = start["id"]!.GetValue<string>(),
                ["type"] = "agent.step.done",
                ["seq"] = 1,
                ["correlation_id"] = correlation,
                ["payload"] = new JsonObject
                {
                    ["text"] = "done",
                    ["tool_calls"] = new JsonArray(),
                    ["finish_reason"] = "stop",
                    ["usage"] = new JsonObject(),
                    ["prompt_receipt"] = PromptReceiptJson(PromptContextFixture.Create()),
                },
            }.ToJsonString());
        });

        await using var runner = new LoopbackModelStepRunner(
            new AgentWorkerClientOptions(new Uri(_url), _token));
        var request = new ModelStepRequest(
            new RunId("run_44444444444444444444444444444444"),
            new ModelStepId("step_44444444444444444444444444444444"),
            new ModelContextSnapshot(
                [ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "prompt")],
                Array.Empty<byte>()),
            PromptContextFixture.Create(),
            AgentPermissionPreset.Standard,
            1,
            new string('e', 64));

        var error = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("invalid_step", error.Code);
    }

    [Fact]
    public async Task RunStepAsync_advertises_every_runtime_tool_with_its_scope()
    {
        var observedStart = new TaskCompletionSource<JsonObject>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await StartServer(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var hello = JsonNode.Parse(await ReceiveTextAsync(socket)) as JsonObject
                ?? throw new InvalidOperationException();
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = hello["id"]!.GetValue<string>(),
                ["type"] = "hello.ok",
                ["seq"] = 0,
                ["payload"] = new JsonObject
                {
                    ["version"] = 2,
                    ["capabilities"] = new JsonArray("agent.step"),
                },
            }.ToJsonString());

            var start = JsonNode.Parse(await ReceiveTextAsync(socket)) as JsonObject
                ?? throw new InvalidOperationException();
            observedStart.TrySetResult(start);
            var correlation = start["correlation_id"]!.GetValue<string>();
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = start["id"]!.GetValue<string>(),
                ["type"] = "agent.step.done",
                ["seq"] = 1,
                ["correlation_id"] = correlation,
                ["payload"] = new JsonObject
                {
                    ["text"] = "ok",
                    ["tool_calls"] = new JsonArray(),
                    ["finish_reason"] = "stop",
                    ["prompt_receipt"] = PromptReceiptJson(PromptContextFixture.Create()),
                },
            }.ToJsonString());
        });

        var options = new AgentWorkerClientOptions(new Uri(_url), _token);
        var runner = new LoopbackModelStepRunner(options);
        var context = new ModelContextSnapshot(
            [ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "prompt")],
            Array.Empty<byte>());
        var request = new ModelStepRequest(
            new RunId("run_33333333333333333333333333333333"),
            new ModelStepId("step_33333333333333333333333333333333"),
            context,
            PromptContextFixture.Create(),
            AgentPermissionPreset.FullAccess,
            1,
            new string('c', 64));

        await runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None);

        var observed = await observedStart.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(observed);
        var settingsNode = observed["payload"]!.AsObject()["settings"];
        Assert.True(settingsNode is not null, observed.ToJsonString());
        var settings = settingsNode!.AsObject();
        Assert.False(settings.ContainsKey("allow_web_search"));
        var tools = settings["model_tools"]!.AsArray()
            .Select(item => item!.AsObject())
            .ToDictionary(item => item["tool_id"]!.GetValue<string>(), StringComparer.Ordinal);

        Assert.Equal(
            new[]
            {
                "read", "grep", "glob", "write", "edit", "bash", "web_search", "web_fetch",
            }.Order(StringComparer.Ordinal),
            tools.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["workspace.read"],
            tools["read"]["scopes"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(
            ["workspace.write"],
            tools["write"]["scopes"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(
            ["terminal.execute"],
            tools["bash"]["scopes"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(
            ["network.search"],
            tools["web_search"]["scopes"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(
            ["network.fetch"],
            tools["web_fetch"]["scopes"]!.AsArray().Select(item => item!.GetValue<string>()));
    }

    [Fact]
    public async Task Cancellation_emits_agent_step_cancel_before_closing()
    {
        var cancelReceived = new TaskCompletionSource<string>();
        await StartServer(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64 * 1024];
            var hello = await ReceiveTextAsync(socket);
            var helloJson = JsonNode.Parse(hello) as JsonObject ?? throw new InvalidOperationException();
            var helloId = helloJson["id"]!.GetValue<string>();
            var helloOk = new JsonObject
            {
                ["v"] = 2,
                ["id"] = helloId,
                ["type"] = "hello.ok",
                ["seq"] = 0,
                ["payload"] = new JsonObject { ["version"] = 2, ["capabilities"] = new JsonArray("agent.step") },
            };
            await SendTextAsync(socket, helloOk.ToJsonString());

            var start = await ReceiveTextAsync(socket);
            var startJson = JsonNode.Parse(start) as JsonObject ?? throw new InvalidOperationException();
            var requestId = startJson["id"]!.GetValue<string>();

            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = requestId,
                ["type"] = "agent.step.delta",
                ["seq"] = 1,
                ["correlation_id"] = startJson["correlation_id"]!.GetValue<string>(),
                ["payload"] = new JsonObject { ["text"] = "buffered" },
            }.ToJsonString());

            // Wait for cancel
            var next = await ReceiveTextAsync(socket);
            var nextJson = JsonNode.Parse(next) as JsonObject ?? throw new InvalidOperationException();
            if (nextJson["type"]!.GetValue<string>() == "agent.step.cancel" && nextJson["id"]!.GetValue<string>() == requestId)
            {
                cancelReceived.TrySetResult(next);
                // Send cancelled error as final
                var err = new JsonObject
                {
                    ["v"] = 2,
                    ["id"] = requestId,
                    ["type"] = "error",
                    ["seq"] = 1,
                    ["payload"] = new JsonObject { ["code"] = "cancelled", ["retryable"] = false },
                };
                await SendTextAsync(socket, err.ToJsonString());
            }
            else
            {
                cancelReceived.TrySetException(new Exception($"expected cancel but got {next}"));
            }
        });

        var options = new AgentWorkerClientOptions(new Uri(_url), _token);
        var runner = new LoopbackModelStepRunner(options);
        var context = new ModelContextSnapshot(
            new[] { RuAgent.Harness.Core.Context.ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "prompt") },
            Array.Empty<byte>());
        var request = new ModelStepRequest(new RunId("run_22222222222222222222222222222222"), new ModelStepId("step_22222222222222222222222222222222"), context, AgentPermissionPreset.Standard, 1, new string('b', 64));
        using var cts = new CancellationTokenSource();
        var deltas = new List<string>();
        var runTask = runner.RunStepAsync(request, delta =>
        {
            deltas.Add(delta.Content);
            return ValueTask.CompletedTask;
        }, cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        try
        {
            await runTask;
            Assert.Fail("expected cancellation");
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        catch (WorkerProtocolException ex) when (ex.Code == "cancelled")
        {
            // also acceptable if server sends cancelled error
        }

        var cancelJson = await cancelReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("agent.step.cancel", cancelJson);
        Assert.Empty(deltas);
    }

    [Fact]
    public async Task RunStepAsync_retries_retryable_worker_error_with_fresh_attempt()
    {
        var starts = 0;
        await StartServer(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var hello = JsonNode.Parse(await ReceiveTextAsync(socket))!.AsObject();
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = hello["id"]!.GetValue<string>(),
                ["type"] = "hello.ok",
                ["seq"] = 0,
                ["payload"] = new JsonObject { ["version"] = 2, ["capabilities"] = new JsonArray("agent.step") },
            }.ToJsonString());

            var start = JsonNode.Parse(await ReceiveTextAsync(socket))!.AsObject();
            var requestId = start["id"]!.GetValue<string>();
            var correlation = start["correlation_id"]!.GetValue<string>();
            if (Interlocked.Increment(ref starts) == 1)
            {
                await SendTextAsync(socket, new JsonObject
                {
                    ["v"] = 2,
                    ["id"] = requestId,
                    ["type"] = "error",
                    ["seq"] = 1,
                    ["correlation_id"] = correlation,
                    ["payload"] = new JsonObject { ["code"] = "provider_rate_limited", ["retryable"] = true },
                }.ToJsonString());
                return;
            }

            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = requestId,
                ["type"] = "agent.step.delta",
                ["seq"] = 1,
                ["correlation_id"] = correlation,
                ["payload"] = new JsonObject { ["text"] = "recovered" },
            }.ToJsonString());
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = requestId,
                ["type"] = "agent.step.done",
                ["seq"] = 2,
                ["correlation_id"] = correlation,
                ["payload"] = new JsonObject
                {
                    ["text"] = "recovered",
                    ["tool_calls"] = new JsonArray(),
                    ["finish_reason"] = "stop",
                    ["prompt_receipt"] = PromptReceiptJson(),
                },
            }.ToJsonString());
        });

        await using var runner = new LoopbackModelStepRunner(new AgentWorkerClientOptions(new Uri(_url), _token))
        {
            StepRetryBackoffsOverride = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)],
        };
        var request = new ModelStepRequest(
            new RunId("run_33333333333333333333333333333333"),
            new ModelStepId("step_33333333333333333333333333333333"),
            new ModelContextSnapshot(
                [ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "prompt")],
                Array.Empty<byte>()),
            AgentPermissionPreset.Standard,
            1,
            new string('c', 64));
        var deltas = new List<string>();
        var result = await runner.RunStepAsync(
            request,
            delta => { deltas.Add(delta.Content); return ValueTask.CompletedTask; },
            CancellationToken.None);

        Assert.Equal("recovered", result.Step.Text);
        Assert.Equal(["recovered"], deltas);
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task RunStepAsync_retries_rejected_tool_call_with_feedback_note()
    {
        var starts = 0;
        string? secondPrompt = null;
        await StartServer(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var hello = JsonNode.Parse(await ReceiveTextAsync(socket))!.AsObject();
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = hello["id"]!.GetValue<string>(),
                ["type"] = "hello.ok",
                ["seq"] = 0,
                ["payload"] = new JsonObject { ["version"] = 2, ["capabilities"] = new JsonArray("agent.step") },
            }.ToJsonString());

            var start = JsonNode.Parse(await ReceiveTextAsync(socket))!.AsObject();
            var requestId = start["id"]!.GetValue<string>();
            var correlation = start["correlation_id"]!.GetValue<string>();
            if (Interlocked.Increment(ref starts) == 1)
            {
                await SendTextAsync(socket, new JsonObject
                {
                    ["v"] = 2,
                    ["id"] = requestId,
                    ["type"] = "error",
                    ["seq"] = 1,
                    ["correlation_id"] = correlation,
                    ["payload"] = new JsonObject { ["code"] = "model_tool_call_invalid", ["retryable"] = true },
                }.ToJsonString());
                return;
            }

            secondPrompt = start["payload"]!["prompt"]!.GetValue<string>();
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = requestId,
                ["type"] = "agent.step.done",
                ["seq"] = 1,
                ["correlation_id"] = correlation,
                ["payload"] = new JsonObject
                {
                    ["text"] = "recovered",
                    ["tool_calls"] = new JsonArray(),
                    ["finish_reason"] = "stop",
                    ["prompt_receipt"] = PromptReceiptJson(),
                },
            }.ToJsonString());
        });

        await using var runner = new LoopbackModelStepRunner(new AgentWorkerClientOptions(new Uri(_url), _token))
        {
            StepRetryBackoffsOverride = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)],
        };
        var request = new ModelStepRequest(
            new RunId("run_55555555555555555555555555555555"),
            new ModelStepId("step_55555555555555555555555555555555"),
            new ModelContextSnapshot(
                [ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "prompt")],
                Array.Empty<byte>()),
            AgentPermissionPreset.Standard,
            1,
            new string('c', 64));

        var result = await runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal("recovered", result.Step.Text);
        Assert.Equal(2, starts);
        Assert.NotNull(secondPrompt);
        Assert.Contains("[harness]", secondPrompt, StringComparison.Ordinal);
        Assert.Contains("arguments were incomplete or invalid", secondPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunStepAsync_does_not_retry_non_retryable_worker_error()
    {
        var starts = 0;
        await StartServer(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var hello = JsonNode.Parse(await ReceiveTextAsync(socket))!.AsObject();
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = hello["id"]!.GetValue<string>(),
                ["type"] = "hello.ok",
                ["seq"] = 0,
                ["payload"] = new JsonObject { ["version"] = 2, ["capabilities"] = new JsonArray("agent.step") },
            }.ToJsonString());

            var start = JsonNode.Parse(await ReceiveTextAsync(socket))!.AsObject();
            Interlocked.Increment(ref starts);
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = start["id"]!.GetValue<string>(),
                ["type"] = "error",
                ["seq"] = 1,
                ["correlation_id"] = start["correlation_id"]!.GetValue<string>(),
                ["payload"] = new JsonObject { ["code"] = "generation_failed", ["retryable"] = false },
            }.ToJsonString());
        });

        await using var runner = new LoopbackModelStepRunner(new AgentWorkerClientOptions(new Uri(_url), _token))
        {
            StepRetryBackoffsOverride = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)],
        };
        var request = new ModelStepRequest(
            new RunId("run_44444444444444444444444444444444"),
            new ModelStepId("step_44444444444444444444444444444444"),
            new ModelContextSnapshot(
                [ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "prompt")],
                Array.Empty<byte>()),
            AgentPermissionPreset.Standard,
            1,
            new string('d', 64));

        var error = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("generation_failed", error.Code);
        Assert.False(error.Retryable);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task RunStepAsync_gives_up_after_bounded_retryable_failures()
    {
        var starts = 0;
        await StartServer(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var hello = JsonNode.Parse(await ReceiveTextAsync(socket))!.AsObject();
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = hello["id"]!.GetValue<string>(),
                ["type"] = "hello.ok",
                ["seq"] = 0,
                ["payload"] = new JsonObject { ["version"] = 2, ["capabilities"] = new JsonArray("agent.step") },
            }.ToJsonString());

            var start = JsonNode.Parse(await ReceiveTextAsync(socket))!.AsObject();
            Interlocked.Increment(ref starts);
            await SendTextAsync(socket, new JsonObject
            {
                ["v"] = 2,
                ["id"] = start["id"]!.GetValue<string>(),
                ["type"] = "error",
                ["seq"] = 1,
                ["correlation_id"] = start["correlation_id"]!.GetValue<string>(),
                ["payload"] = new JsonObject { ["code"] = "provider_rate_limited", ["retryable"] = true },
            }.ToJsonString());
        });

        await using var runner = new LoopbackModelStepRunner(new AgentWorkerClientOptions(new Uri(_url), _token))
        {
            StepRetryBackoffsOverride = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)],
        };
        var request = new ModelStepRequest(
            new RunId("run_55555555555555555555555555555555"),
            new ModelStepId("step_55555555555555555555555555555555"),
            new ModelContextSnapshot(
                [ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "prompt")],
                Array.Empty<byte>()),
            AgentPermissionPreset.Standard,
            1,
            new string('e', 64));

        var error = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("provider_rate_limited", error.Code);
        Assert.True(error.Retryable);
        Assert.Equal(3, starts);
    }

    private static JsonObject PromptReceiptJson(SystemPromptContext? context = null)
    {
        var blocks = new JsonArray(
            new JsonObject { ["block_id"] = "ru.persona", ["sha256"] = new string('0', 64) },
            new JsonObject { ["block_id"] = "agent.contract", ["sha256"] = new string('0', 64) },
            new JsonObject { ["block_id"] = "environment", ["sha256"] = new string('0', 64) },
            new JsonObject { ["block_id"] = "profile", ["sha256"] = new string('0', 64) },
            new JsonObject { ["block_id"] = "capabilities", ["sha256"] = new string('0', 64) },
            new JsonObject { ["block_id"] = "runtime", ["sha256"] = new string('0', 64) });
        if (context is not null)
        {
            var insertionIndex = 3;
            foreach (var instruction in context.InstructionBlocks)
            {
                blocks.Insert(
                    insertionIndex++,
                    new JsonObject
                    {
                        ["block_id"] = $"instruction.{instruction.SourceId}",
                        ["sha256"] = instruction.ContentSha256,
                    });
            }
        }

        return new JsonObject
        {
            ["composer_version"] = "1.0.0",
            ["prompt_sha256"] = new string('0', 64),
            ["block_digests"] = blocks,
        };
    }

    private static class PromptContextFixture
    {
        public static SystemPromptContext Create()
        {
            const string content = "root guidance";
            return new SystemPromptContext(
                1,
                "ru-agent",
                "standard",
                new PromptEnvironment("test-model", "windows", "vi-VN", "Asia/Bangkok", "2026-08-29", "LLM"),
                [new PromptInstructionBlock(
                    "root:Ru.md",
                    "Ru.md",
                    content,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant())],
                AgentToolDefinitionCatalog.Get()
                    .Select(definition => Capability(definition.ToolId))
                    .ToArray(),
                new PromptRuntimeContext(
                    null,
                    null,
                    null,
                    null,
                    [new PromptReference("tool_result", "untrusted", "untrusted tool result")]));
        }

        private static PromptCapability Capability(string toolId)
        {
            var definition = AgentToolDefinitionCatalog.Find(toolId, "1.0.0")!;
            var schema = definition.InputSchema.ToJsonString();
            return new PromptCapability(
                definition.ToolId,
                definition.ToolVersion,
                definition.Description,
                definition.Scope,
                schema,
                AgentToolDefinitionCatalog.ComputeSchemaDigest(definition.InputSchema),
                definition.ExampleArguments?.ToJsonString(),
                "enabled");
        }
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new System.IO.MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new Exception("closed");
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }

    private static async Task SendTextAsync(WebSocket socket, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }
}
