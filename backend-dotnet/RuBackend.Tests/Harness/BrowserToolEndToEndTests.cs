extern alias Broker;

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using RuBackend.Harness;
using Xunit;
using ChromiumBrowserEngine = Broker::RuAgent.Broker.Tools.Browser.ChromiumBrowserEngine;

namespace RuBackend.Tests.Harness;

/// <summary>
/// The built-in browser tool from the Agent loop down to a real Chromium page (ADR 0055): the
/// Host advertises it to a scripted root run, the Broker launches its own isolated browser, and
/// the real page state proves the click and type reached the DOM. Skipped when no Edge/Chrome
/// is installed.
/// </summary>
[Collection(nameof(TerminalProcessHeavyCollection))]
public sealed class BrowserToolEndToEndTests
{
    [BrowserEndToEndFact]
    public async Task A_scripted_agent_run_drives_a_real_browser_and_the_page_changes()
    {
        await using var page = await TestPageServer.StartAsync();
        using var tmp = TempWorkspace.Create();
        var steps = new Queue<AssistantStep>();
        steps.Enqueue(ToolStep("browser", new { action = "open", url = page.Url }));
        // One batched tool call proves the Agent loop pays one model step for four page actions.
        steps.Enqueue(ToolStep("browser", new
        {
            action = "batch",
            steps = new object[]
            {
                new { action = "snapshot" },
                new { action = "click", @ref = "e1" },
                new { action = "type", @ref = "e2", text = "Ada" },
                new
                {
                    action = "eval",
                    expression = "fetch('/beacon?count=' + document.getElementById('count').textContent + '&typed=' + encodeURIComponent(document.getElementById('name').value))",
                },
            },
        }));
        steps.Enqueue(new AssistantStep("browser check finished", [], AssistantFinishReason.Stop, 0));

        using var factory = new BackendWebApplicationFactory(
            dataPaths: tmp.DataPaths,
            modelStepRunner: new ScriptedStepRunner(steps));
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);

        // The browser tool's open requires the external-terminal decision; full_access lets the
        // run proceed without an approval prompt while the rest of the stack stays production.
        using var interactive = factory.CreateAuthenticatedClient();
        interactive.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");
        using (var preset = await interactive.PostAsJsonAsync("/agent/v1/policies", new
        {
            kind = "session_preset",
            session_id = sessionId,
            preset = "full_access",
            expected_version = 0,
        }))
        {
            Assert.Equal(HttpStatusCode.OK, preset.StatusCode);
        }

        using var created = await client.PostAsJsonAsync("/agent/v1/runs", new
        {
            session_id = sessionId,
            workspace_path = tmp.Path,
            prompt = "open the browser and click the increment button",
            preset = "full_access",
            preset_selection_version = 1,
            idempotency_key = "browser-e2e-" + Guid.NewGuid().ToString("N"),
        });
        var createdBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.IsSuccessStatusCode, $"create run returned {(int)created.StatusCode}: {createdBody}");
        using var createdJson = JsonDocument.Parse(createdBody);
        var runId = createdJson.RootElement.GetProperty("run_id").GetString()!;
        if (createdJson.RootElement.TryGetProperty("trust_request_id", out var trustRequest)
            && trustRequest.ValueKind == JsonValueKind.String)
        {
            using var trust = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/trust", new
            {
                trust_request_id = trustRequest.GetString(),
                display_identity_hash = createdJson.RootElement.GetProperty("display_identity_hash").GetString(),
                decision = "approve",
            });
            trust.EnsureSuccessStatusCode();
        }

        var status = await WaitForTerminalStatusAsync(client, runId);
        Assert.True(status == "completed", $"final status={status}; browser tool did not complete");

        var beacon = await page.WaitForBeaconAsync(TimeSpan.FromSeconds(15));
        Assert.NotNull(beacon);
        Assert.Contains("count=1", beacon, StringComparison.Ordinal);
        Assert.Contains("typed=Ada", beacon, StringComparison.Ordinal);

        using var events = await client.GetAsync($"/agent/v1/runs/{runId}/events");
        var eventsBody = await events.Content.ReadAsStringAsync();
        Assert.True(events.IsSuccessStatusCode);
        Assert.Contains("tool.execution_completed", eventsBody, StringComparison.Ordinal);
        Assert.DoesNotContain("browser_not_open", eventsBody, StringComparison.Ordinal);
        Assert.DoesNotContain("browser_unavailable", eventsBody, StringComparison.Ordinal);
        Assert.DoesNotContain("browser_invalid_url", eventsBody, StringComparison.Ordinal);
    }

    private static AssistantStep ToolStep(string toolId, object arguments) => new(
        null,
        [
            new ModelToolCall(
                "provider_call_" + Guid.NewGuid().ToString("N")[..8],
                toolId,
                "1.0.0",
                JsonDocument.Parse(JsonSerializer.Serialize(arguments)).RootElement),
        ],
        AssistantFinishReason.ToolCalls,
        0);

    private static async Task<string?> WaitForTerminalStatusAsync(HttpClient client, string runId)
    {
        string? status = null;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            using var response = await client.GetAsync($"/agent/v1/runs/{runId}");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            status = document.RootElement.GetProperty("status").GetString();
            if (status is "completed" or "failed")
            {
                break;
            }
        }

        return status;
    }

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        using var response = await client.PostAsync("/sessions/new", null);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("session_id").GetString()!;
    }

    private sealed class ScriptedStepRunner : IModelStepRunner
    {
        private readonly Queue<AssistantStep> _steps;

        public ScriptedStepRunner(Queue<AssistantStep> steps) => _steps = steps;

        public Task<ModelStepResult> RunStepAsync(
            ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta,
            CancellationToken cancellationToken)
        {
            _ = publishDelta;
            _ = cancellationToken;
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("no scripted step left");
            }

            var receipt = SystemPromptComposer.Compose(request.PromptContext).Receipt;
            return Task.FromResult(new ModelStepResult(_steps.Dequeue(), receipt));
        }
    }

    /// <summary>A loopback page whose increment button, name field and beacon make the DOM effect visible.</summary>
    private sealed class TestPageServer : IAsyncDisposable
    {
        private const string Html = """
            <!doctype html>
            <html>
            <head><title>Ru browser e2e</title></head>
            <body>
              <h1>Browser e2e</h1>
              <button id="inc" onclick="document.getElementById('count').textContent = String(Number(document.getElementById('count').textContent) + 1)">Increment</button>
              <div id="count">0</div>
              <input id="name" placeholder="Your name">
            </body>
            </html>
            """;

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;
        private readonly TaskCompletionSource<string> _beacon =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TestPageServer()
        {
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
            _serving = Task.Run(ServeAsync);
        }

        public string Url { get; }

        public static Task<TestPageServer> StartAsync() => Task.FromResult(new TestPageServer());

        public async Task<string?> WaitForBeaconAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(_beacon.Task, Task.Delay(timeout));
            return completed == _beacon.Task ? await _beacon.Task : null;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _serving;
            }
            catch (Exception)
            {
                // The accept loop ends when the listener is stopped.
            }

            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => RespondAsync(client));
            }
        }

        private async Task RespondAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var request = new StringBuilder();
                    var buffer = new byte[4096];
                    while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer);
                        if (read == 0) return;
                        request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        if (request.Length > 16 * 1024) return;
                    }

                    var firstLine = request.ToString().Split("\r\n", 2, StringSplitOptions.None)[0];
                    if (firstLine.Contains("/beacon", StringComparison.Ordinal))
                    {
                        _beacon.TrySetResult(firstLine);
                    }

                    var body = Encoding.UTF8.GetBytes(Html);
                    var head = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n"
                        + "Content-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head);
                    await stream.WriteAsync(body);
                }
                catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
                {
                    // A cancelled page request is not a test failure.
                }
            }
        }
    }

    private sealed class TempWorkspace : IDisposable
    {
        private readonly string _root;

        private TempWorkspace(string root, string workspacePath, RuBackend.Business.DataPaths dataPaths)
        {
            _root = root;
            Path = workspacePath;
            DataPaths = dataPaths;
        }

        public string Path { get; }

        public RuBackend.Business.DataPaths DataPaths { get; }

        public static TempWorkspace Create()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ru-browser-e2e-" + Guid.NewGuid().ToString("N"));
            var workspace = System.IO.Path.Combine(root, "ws");
            Directory.CreateDirectory(workspace);
            var dataRoot = System.IO.Path.Combine(root, "data");
            Directory.CreateDirectory(dataRoot);
            return new TempWorkspace(
                root,
                workspace,
                new RuBackend.Business.DataPaths(
                    System.IO.Path.Combine(dataRoot, "settings.json"),
                    System.IO.Path.Combine(dataRoot, "sessions"),
                    System.IO.Path.Combine(dataRoot, "activity.jsonl")));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>A fact that skips when no Edge/Chrome installation is available.</summary>
public sealed class BrowserEndToEndFactAttribute : FactAttribute
{
    public BrowserEndToEndFactAttribute()
    {
        if (Broker::RuAgent.Broker.Tools.Browser.BrowserExecutableDiscovery.Resolve() is null)
        {
            Skip = "No supported Chromium or Firefox installation is available.";
        }
    }
}
