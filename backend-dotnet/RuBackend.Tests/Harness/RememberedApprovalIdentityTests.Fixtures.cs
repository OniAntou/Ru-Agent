extern alias Broker;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Prompting;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Policy;
using Xunit;
using BrokerComputerService = Broker::RuAgent.Broker.Tools.Computer.ComputerUseService;
using BrokerDesktopController = Broker::RuAgent.Broker.Tools.Computer.IDesktopController;
using BrokerDesktopMonitor = Broker::RuAgent.Broker.Tools.Computer.DesktopMonitor;
using BrokerDesktopWindow = Broker::RuAgent.Broker.Tools.Computer.DesktopWindow;
using BrokerKeyChord = Broker::RuAgent.Broker.Tools.Computer.KeyChord;
using BrokerMcpServerHost = Broker::RuAgent.Broker.Tools.Mcp.McpServerHost;
using BrokerPixelRect = Broker::RuAgent.Broker.Tools.Computer.PixelRect;
using BrokerPointerButton = Broker::RuAgent.Broker.Tools.Computer.PointerButton;
using BrokerStopHotkey = Broker::RuAgent.Broker.Tools.Computer.IComputerStopHotkey;

namespace RuBackend.Tests.Harness;

/// <summary>
/// Shared fixtures for the remembered-approval identity cases: a per-test host
/// over the real Host -> Broker path, a stepped model, a loopback MCP server
/// speaking Streamable HTTP, and seams for the desktop and for a failed Broker
/// grant. Each seam replaces one registration; nothing in production is changed.
/// </summary>
public sealed partial class RememberedApprovalIdentityTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    private sealed record StartedRun(string Id, string WorkspaceId);

    private sealed record RunView(string RunId, string? ApprovalId, string Status, IReadOnlyList<JsonElement> Events)
    {
        public int EventCount(string eventType) =>
            Events.Count(evt => evt.TryGetProperty("event_type", out var type) && type.GetString() == eventType);
    }

    /// <summary>
    /// One host with its own data root, workspace, user profile and optional seams.
    /// Disposing it stops the host, the MCP server and removes the temp root.
    /// </summary>
    private sealed class Scenario : IAsyncDisposable
    {
        private readonly string _root;
        private readonly RuBackend.Business.DataPaths _dataPaths;
        private readonly FakeMcpServers? _mcp;

        private Scenario(string root, string workspace, RuBackend.Business.DataPaths dataPaths,
            FakeMcpServers? mcp, SteppedRunner runner, BackendWebApplicationFactory factory)
        {
            _root = root;
            _dataPaths = dataPaths;
            _mcp = mcp;
            Root = root;
            Workspace = workspace;
            Runner = runner;
            Factory = factory;
            Client = factory.CreateAuthenticatedClient();
        }

        public string Root { get; }

        public string Workspace { get; }

        public SteppedRunner Runner { get; }

        public BackendWebApplicationFactory Factory { get; }

        public HttpClient Client { get; }

        /// <param name="mcp">Declare servers "alpha" and "beta" in the test user's ~/.ru/mcp.json, served by a loopback fake.</param>
        /// <param name="computer">Replace the desktop controller with a headless one so request_access and friends run without a real desktop.</param>
        /// <param name="failGrants">Make every Broker approval bridge fail with a transport error after the Host has resolved the approval.</param>
        public static async Task<Scenario> StartAsync(bool mcp = false, bool computer = false, bool failGrants = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-remembered-" + Guid.NewGuid().ToString("N"));
            var workspace = Path.Combine(root, "ws");
            var dataRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(dataRoot);
            var dataPaths = RuBackend.Business.DataPaths.Resolve(root, root, root, dataRoot, Path.Combine(root, "logs"));

            FakeMcpServers? server = null;
            string? userHome = null;
            if (mcp)
            {
                server = await FakeMcpServers.StartAsync();
                userHome = Path.Combine(root, "home");
                WriteMcpConfiguration(userHome, server.BaseUrl);
            }

            var runner = new SteppedRunner();
            var factory = new BackendWebApplicationFactory(
                dataPaths: dataPaths,
                modelStepRunner: runner,
                configureServices: services =>
                {
                    if (userHome is not null)
                    {
                        services.Replace(ServiceDescriptor.Singleton<BrokerMcpServerHost>(
                            new BrokerMcpServerHost(() => userHome, _ => null)));
                    }

                    if (computer)
                    {
                        services.Replace(ServiceDescriptor.Singleton<BrokerComputerService>(
                            new BrokerComputerService(new HeadlessDesktop(), new InertStopHotkey())));
                    }

                    if (failGrants)
                    {
                        ReplaceGatewayFactoryWithFailingGrants(services);
                    }
                });
            return new Scenario(root, workspace, dataPaths, server, runner, factory);
        }

        public async Task<string> NewSessionAsync()
        {
            using var response = await Client.PostAsync("/sessions/new", null);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            return json.GetProperty("session_id").GetString()!;
        }

        /// <summary>Creates a standard-preset run in the workspace and approves its trust request, as the existing approval suites do.</summary>
        public async Task<StartedRun> StartRunAsync(string sessionId, string prompt)
        {
            var body = new
            {
                session_id = sessionId,
                workspace_path = Workspace,
                prompt,
                preset = "standard",
                idempotency_key = Guid.NewGuid().ToString("N"),
            };
            using var response = await Client.PostAsJsonAsync("/agent/v1/runs", body);
            response.EnsureSuccessStatusCode();
            var created = await response.Content.ReadFromJsonAsync<JsonElement>();
            var runId = created.GetProperty("run_id").GetString()!;
            if (created.TryGetProperty("trust_request_id", out var trustId)
                && trustId.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(trustId.GetString()))
            {
                using var trust = await Client.PostAsJsonAsync($"/agent/v1/runs/{runId}/trust", new
                {
                    trust_request_id = trustId.GetString(),
                    display_identity_hash = created.GetProperty("display_identity_hash").GetString(),
                    decision = "approve",
                });
                trust.EnsureSuccessStatusCode();
            }

            return new StartedRun(runId, created.GetProperty("workspace_id").GetString()!);
        }

        /// <summary>
        /// Waits until the run either parks on an approval request or reaches a terminal
        /// state. The approval event is durable before the run parks, so a terminal status
        /// with no approval event means the call was not asked.
        /// </summary>
        public async Task<RunView> WaitForAskOrEndAsync(string runId)
        {
            var deadline = DateTime.UtcNow.Add(Deadline);
            while (true)
            {
                var events = await ReadEventsAsync(runId);
                var approval = LatestApprovalId(events);
                var status = await GetStatusAsync(runId);
                if (approval is not null)
                {
                    return new RunView(runId, approval, status, events);
                }

                if (IsTerminal(status))
                {
                    var settled = await ReadEventsAsync(runId);
                    return new RunView(runId, LatestApprovalId(settled), status, settled);
                }

                if (DateTime.UtcNow >= deadline)
                {
                    return new RunView(runId, null, status, events);
                }

                await Task.Delay(200);
            }
        }

        public async Task<RunView> WaitForEndAsync(string runId)
        {
            var deadline = DateTime.UtcNow.Add(Deadline);
            string status;
            do
            {
                await Task.Delay(200);
                status = await GetStatusAsync(runId);
            }
            while (!IsTerminal(status) && DateTime.UtcNow < deadline);

            var events = await ReadEventsAsync(runId);
            return new RunView(runId, LatestApprovalId(events), status, events);
        }

        public async Task<HttpResponseMessage> DecideAsync(string runId, string approvalId, string command)
        {
            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
            return await Client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { command });
        }

        public async Task<int> ProjectRuleCountAsync(string workspaceId)
        {
            using var response = await Client.GetAsync(
                $"/agent/v1/policies?scope=project&workspace_id={Uri.EscapeDataString(workspaceId)}");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            return json.GetProperty("items").GetArrayLength();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Factory.Dispose();
            try
            {
                SqliteTestPools.ClearHarnessDatabase(_dataPaths.AgentHarnessDatabase);
            }
            catch
            {
            }

            if (_mcp is not null)
            {
                await _mcp.DisposeAsync();
            }

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }

        private async Task<IReadOnlyList<JsonElement>> ReadEventsAsync(string runId)
        {
            using var response = await Client.GetAsync($"/agent/v1/runs/{runId}/events?live=0");
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            var events = new List<JsonElement>();
            foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    continue;
                }

                var payload = line["data: ".Length..];
                if (payload.Contains("\"event_type\"", StringComparison.Ordinal))
                {
                    events.Add(JsonDocument.Parse(payload).RootElement.Clone());
                }
            }

            return events;
        }

        private async Task<string> GetStatusAsync(string runId)
        {
            using var response = await Client.GetAsync($"/agent/v1/runs/{runId}");
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            return json.GetProperty("status").GetString()!;
        }

        private static bool IsTerminal(string status) =>
            status is "completed" or "failed" or "cancelled";

        private static string? LatestApprovalId(IReadOnlyList<JsonElement> events)
        {
            string? latest = null;
            foreach (var evt in events)
            {
                if (evt.TryGetProperty("event_type", out var type)
                    && type.GetString() == "approval.requested"
                    && evt.TryGetProperty("approval_id", out var approvalId))
                {
                    latest = approvalId.GetString();
                }
            }

            return latest;
        }

        private static void WriteMcpConfiguration(string userHome, string baseUrl)
        {
            Directory.CreateDirectory(Path.Combine(userHome, ".ru"));
            var json = new JsonObject
            {
                ["servers"] = new JsonObject
                {
                    ["alpha"] = new JsonObject { ["transport"] = "streamable_http", ["url"] = baseUrl + "/alpha/mcp" },
                    ["beta"] = new JsonObject { ["transport"] = "streamable_http", ["url"] = baseUrl + "/beta/mcp" },
                },
            };
            File.WriteAllText(Path.Combine(userHome, ".ru", "mcp.json"), json.ToJsonString());
        }
    }

    /// <summary>
    /// Returns scripted model steps, one run at a time. Each run scripts its own
    /// steps right before it starts, so a parked or denied run cannot leave steps
    /// behind for the next one.
    /// </summary>
    private sealed class SteppedRunner : RuAgent.Harness.Core.IModelStepRunner
    {
        private readonly object _gate = new();
        private readonly Queue<AssistantStep> _steps = new();

        public void Script(params AssistantStep[] steps)
        {
            lock (_gate)
            {
                _steps.Clear();
                foreach (var step in steps)
                {
                    _steps.Enqueue(step);
                }
            }
        }

        public Task<ModelStepResult> RunStepAsync(
            ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta,
            CancellationToken cancellationToken)
        {
            AssistantStep step;
            lock (_gate)
            {
                step = _steps.Count > 0 ? _steps.Dequeue() : FinalStep("script exhausted; done");
            }

            return Task.FromResult(new ModelStepResult(
                step,
                SystemPromptComposer.Compose(request.PromptContext, nativeToolCalling: true).Receipt));
        }
    }

    private static AssistantStep ToolCall(string toolId, string argumentsJson) =>
        new(
            null,
            new[]
            {
                new ModelToolCall("provider_call_1", toolId, "1.0.0", JsonDocument.Parse(argumentsJson).RootElement.Clone()),
            },
            AssistantFinishReason.ToolCalls,
            0);

    private static AssistantStep FinalStep(string text) =>
        new(text, Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, 0);

    private static string McpCall(string server, string tool) =>
        JsonSerializer.Serialize(new { op = "call", server, tool });

    private static string McpListTools(string server) =>
        JsonSerializer.Serialize(new { op = "list_tools", server });

    private static string RequestAccess(params string[] apps) =>
        JsonSerializer.Serialize(new { action = "request_access", apps });

    private static string RunProcess(string executable, string[] arguments, string? workingDirectory = null, int? timeoutMs = null)
    {
        var node = new JsonObject
        {
            ["executable"] = executable,
            ["arguments"] = new JsonArray(arguments.Select(argument => (JsonNode?)JsonValue.Create(argument)).ToArray()),
        };
        if (workingDirectory is not null)
        {
            node["working_directory"] = workingDirectory;
        }

        if (timeoutMs is not null)
        {
            node["timeout_ms"] = timeoutMs.Value;
        }

        return node.ToJsonString();
    }

    private static void ReplaceGatewayFactoryWithFailingGrants(IServiceCollection services)
    {
        var production = services.Last(descriptor => descriptor.ServiceType == typeof(IToolGatewayFactory));
        services.Replace(ServiceDescriptor.Singleton<IToolGatewayFactory>(provider =>
            new GrantTransportFailure((IToolGatewayFactory)production.ImplementationFactory!(provider))));
    }

    /// <summary>
    /// Delegates everything to the production gateway factory except the approval
    /// bridge, which fails the way a dropped Broker transport would.
    /// </summary>
    private sealed class GrantTransportFailure(IToolGatewayFactory inner) : IToolGatewayFactory
    {
        public bool IsConfigured => inner.IsConfigured;

        public Task<IToolGateway> CreateAsync(
            string workspaceRoot,
            PolicySnapshot policySnapshot,
            RunId runId,
            string? agentSessionId,
            CancellationToken cancellationToken) =>
            inner.CreateAsync(workspaceRoot, policySnapshot, runId, agentSessionId, cancellationToken);

        public Task<JsonObject> ListSessionTasksAsync(string runId, string? killExecutionId, CancellationToken cancellationToken) =>
            inner.ListSessionTasksAsync(runId, killExecutionId, cancellationToken);

        public Task<RuBackend.Harness.Broker.BrokerApprovalProbe> ProbeApprovalAsync(string runId, string hostApprovalId, CancellationToken cancellationToken) =>
            inner.ProbeApprovalAsync(runId, hostApprovalId, cancellationToken);

        public Task<(bool Authorized, string? AuthorizationToken, Broker::RuAgent.Broker.Policy.ApprovalRememberPlan? RememberPlan)> RespondApprovalAsync(
            string runId,
            string hostApprovalId,
            string command,
            CancellationToken cancellationToken) =>
            Task.FromException<(bool Authorized, string? AuthorizationToken, Broker::RuAgent.Broker.Policy.ApprovalRememberPlan? RememberPlan)>(
                new ToolTransportException("injected broker transport failure"));

        public Task<bool> RevokeSessionGrantsAsync(string sessionId, CancellationToken cancellationToken) =>
            inner.RevokeSessionGrantsAsync(sessionId, cancellationToken);

        public Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken) =>
            inner.CloseSessionAsync(sessionId, cancellationToken);

        public Task CloseRunAsync(string runId) => inner.CloseRunAsync(runId);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// A desktop with no windows and no input side effects, so request_access can
    /// grant apps and every other action fails at its target check.
    /// </summary>
    private sealed class HeadlessDesktop : BrokerDesktopController
    {
        public bool IsInputDesktopAvailable() => true;

        public IReadOnlyList<BrokerDesktopMonitor> Monitors() =>
            new[] { new BrokerDesktopMonitor(0, new BrokerPixelRect(0, 0, 1920, 1080), true) };

        public IReadOnlyList<BrokerDesktopWindow> Windows() => Array.Empty<BrokerDesktopWindow>();

        public byte[] Capture(BrokerPixelRect bounds) => Array.Empty<byte>();

        public BrokerDesktopWindow? RootWindowAt(int x, int y) => null;

        public BrokerDesktopWindow? ForegroundWindow() => null;

        public BrokerDesktopWindow? TopWindowOf(string appName) => null;

        public bool BringToForeground(nint handle) => false;

        public bool IsShieldedFromInput(int processId) => false;

        public void MovePointer(int x, int y)
        {
        }

        public void Click(int x, int y, BrokerPointerButton button, int count, nint expectedRoot)
        {
        }

        public void Drag(int fromX, int fromY, int toX, int toY, CancellationToken cancellationToken)
        {
        }

        public void Scroll(int x, int y, int deltaX, int deltaY)
        {
        }

        public void TypeText(string text, nint expectedRoot, CancellationToken cancellationToken)
        {
        }

        public void PressChord(BrokerKeyChord chord, nint expectedRoot)
        {
        }

        public IReadOnlySet<int> RuHostProcessIds() => new HashSet<int>();
    }

    private sealed class InertStopHotkey : BrokerStopHotkey
    {
        public bool EnsureRegistered() => true;

        public event Action? Pressed
        {
            add { }
            remove { }
        }
    }

    /// <summary>
    /// Streamable HTTP MCP servers on loopback, one endpoint per declared name
    /// (/alpha/mcp, /beta/mcp). Each exposes read_note and write_note and echoes
    /// the tool it was called with. The port is ephemeral.
    /// </summary>
    private sealed class FakeMcpServers : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeMcpServers(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public string BaseUrl { get; }

        public static async Task<FakeMcpServers> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.MapPost("/{server}/mcp", HandleAsync);
            await app.StartAsync();
            return new FakeMcpServers(app, app.Urls.Single());
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private static async Task<IResult> HandleAsync(string server, HttpContext http)
        {
            var body = await JsonNode.ParseAsync(http.Request.Body) as JsonObject;
            var method = body?["method"]?.GetValue<string>();
            if (body is null || method is null)
            {
                return Results.BadRequest();
            }

            if (body["id"] is null)
            {
                return Results.Accepted();
            }

            var id = body["id"]!.DeepClone();
            JsonNode? result;
            switch (method)
            {
                case "initialize":
                    http.Response.Headers["Mcp-Session-Id"] = "session-" + server;
                    result = new JsonObject
                    {
                        ["protocolVersion"] = "2025-11-25",
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = server, ["version"] = "1.0.0" },
                    };
                    break;
                case "tools/list":
                    result = new JsonObject
                    {
                        ["tools"] = new JsonArray(
                            Tool("read_note"),
                            Tool("write_note")),
                    };
                    break;
                case "tools/call":
                    var tool = body["params"]?["name"]?.GetValue<string>() ?? "unknown";
                    result = new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = $"{server}:{tool}",
                        }),
                    };
                    break;
                default:
                    return Results.Json(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id,
                        ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "method not found" },
                    });
            }

            return Results.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
        }

        private static JsonObject Tool(string name) => new()
        {
            ["name"] = name,
            ["description"] = name.Replace('_', ' '),
            ["inputSchema"] = new JsonObject { ["type"] = "object" },
        };
    }
}
