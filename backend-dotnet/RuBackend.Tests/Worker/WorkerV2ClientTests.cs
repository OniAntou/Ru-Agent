using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Protocol.Harness;
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
                },
            };
            await SendTextAsync(socket, done.ToJsonString());
            await Task.Delay(500);
            tcs.TrySetResult();
        });

        var options = new WorkerClientOptions { Uri = new Uri(_url), Token = _token };
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
        Assert.Equal(2, deltas.Count);
        Assert.Equal("Hello ", deltas[0]);
        Assert.Equal("world", deltas[1]);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
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
                    ["prompt_receipt"] = PromptReceiptJson(),
                },
            }.ToJsonString());
        });

        var options = new WorkerClientOptions { Uri = new Uri(_url), Token = _token };
        var runner = new LoopbackModelStepRunner(options);
        var context = new ModelContextSnapshot(
            [ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "prompt")],
            Array.Empty<byte>());
        var request = new ModelStepRequest(
            new RunId("run_33333333333333333333333333333333"),
            new ModelStepId("step_33333333333333333333333333333333"),
            context,
            AgentPermissionPreset.FullAccess,
            1,
            new string('c', 64));

        await runner.RunStepAsync(request, _ => ValueTask.CompletedTask, CancellationToken.None);

        var observed = await observedStart.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(observed);
        var settingsNode = observed["payload"]!.AsObject()["settings"];
        Assert.True(settingsNode is not null, observed.ToJsonString());
        var settings = settingsNode!.AsObject();
        var tools = settings["model_tools"]!.AsArray()
            .Select(item => item!.AsObject())
            .ToDictionary(item => item["tool_id"]!.GetValue<string>(), StringComparer.Ordinal);

        Assert.Equal(
            new[]
            {
                "files.list", "files.read", "files.glob", "files.search",
                "files.write", "files.edit", "files.delete", "files.restore",
                "terminal.run", "terminal.shell",
            }.Order(StringComparer.Ordinal),
            tools.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["workspace.read"],
            tools["files.read"]["scopes"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(
            ["workspace.write"],
            tools["files.write"]["scopes"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(
            ["terminal.execute"],
            tools["terminal.run"]["scopes"]!.AsArray().Select(item => item!.GetValue<string>()));
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

        var options = new WorkerClientOptions { Uri = new Uri(_url), Token = _token };
        var runner = new LoopbackModelStepRunner(options);
        var context = new ModelContextSnapshot(
            new[] { RuAgent.Harness.Core.Context.ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "prompt") },
            Array.Empty<byte>());
        var request = new ModelStepRequest(new RunId("run_22222222222222222222222222222222"), new ModelStepId("step_22222222222222222222222222222222"), context, AgentPermissionPreset.Standard, 1, new string('b', 64));
        using var cts = new CancellationTokenSource();
        var runTask = runner.RunStepAsync(request, _ => ValueTask.CompletedTask, cts.Token);
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
    }

    private static JsonObject PromptReceiptJson() => new()
    {
        ["composer_version"] = "1.0.0",
        ["prompt_sha256"] = new string('0', 64),
        ["block_digests"] = new JsonArray(
            new JsonObject { ["block_id"] = "ru.persona", ["sha256"] = new string('0', 64) },
            new JsonObject { ["block_id"] = "agent.contract", ["sha256"] = new string('0', 64) },
            new JsonObject { ["block_id"] = "environment", ["sha256"] = new string('0', 64) },
            new JsonObject { ["block_id"] = "profile", ["sha256"] = new string('0', 64) },
            new JsonObject { ["block_id"] = "capabilities", ["sha256"] = new string('0', 64) },
            new JsonObject { ["block_id"] = "runtime", ["sha256"] = new string('0', 64) }),
    };

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
