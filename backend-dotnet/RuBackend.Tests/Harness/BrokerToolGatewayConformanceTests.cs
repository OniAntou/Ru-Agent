extern alias Broker;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebSockets;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Tests.Conformance;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Xunit;
using BrokerTestHostHost = Broker::RuAgent.Broker.Runtime.BrokerHost;

namespace RuBackend.Tests.Harness;

/// <summary>
/// Conformance suite over a real loopback BrokerHost. Every case allocates a loopback
/// port and spawns real processes, so it shares the non-parallel process-heavy
/// collection with the other terminal-spawning suites.
/// </summary>
[Collection(nameof(TerminalProcessHeavyCollection))]
public sealed class BrokerToolGatewayConformanceTests : ToolGatewayConformance
{
    private readonly List<BrokerTestHost> _hosts = [];

    [Theory]
    [InlineData("web_search", ToolAccessKind.ReadOnly)]
    [InlineData("web_fetch", ToolAccessKind.ReadOnly)]
    [InlineData("todo_write", ToolAccessKind.Mutation)]
    [InlineData("future_tool", ToolAccessKind.Terminal)]
    public void Gateway_access_classification_is_conservative(
        string toolId,
        ToolAccessKind expected)
    {
        var actual = global::RuBackend.Harness.Broker.BrokerToolGateway.AccessKindForTool(
            toolId,
            null);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Gateway_multiplexes_independent_rpcs_and_matches_request_ids()
    {
        await using var broker = await DelayedWebSocketBroker.StartAsync();
        var runId = "run_" + new string('a', 31) + "1";
        const string sessionId = "session-concurrency";
        await using var gateway = new global::RuBackend.Harness.Broker.BrokerToolGateway(
            broker.Uri,
            broker.Token,
            sessionId,
            runId,
            () => BuildRunOpenPayload(
                runId,
                sessionId,
                Path.GetTempPath(),
                "plan"));
        var firstProposal = Proposal(new RunId(runId), "read", "read-1");
        var secondProposal = Proposal(new RunId(runId), "read", "read-2");
        var first = gateway.EvaluateAsync(firstProposal, CancellationToken.None);
        var second = gateway.EvaluateAsync(secondProposal, CancellationToken.None);

        var decisions = await Task.WhenAll(first, second)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PolicyDecision.Allow, decisions[0].Decision);
        Assert.Equal(PolicyDecision.Allow, decisions[1].Decision);
        Assert.Equal(
            "auth-" + firstProposal.ToolCallId.Value,
            decisions[0].CanonicalIntentToken);
        Assert.Equal(
            "auth-" + secondProposal.ToolCallId.Value,
            decisions[1].CanonicalIntentToken);
        Assert.Equal(2, broker.MaximumConcurrentResponses);
        Assert.Equal(
            [secondProposal.ToolCallId.Value, firstProposal.ToolCallId.Value],
            broker.ResponseOrder);
    }

    [Fact]
    public async Task Gateway_connection_failure_completes_all_pending_requests()
    {
        await using var broker = await DelayedWebSocketBroker.StartAsync(
            closeAfterEvaluations: true);
        var runId = "run_" + new string('b', 31) + "2";
        const string sessionId = "session-failure";
        await using var gateway = new global::RuBackend.Harness.Broker.BrokerToolGateway(
            broker.Uri,
            broker.Token,
            sessionId,
            runId,
            () => BuildRunOpenPayload(
                runId,
                sessionId,
                Path.GetTempPath(),
                "plan"));
        var first = gateway.EvaluateAsync(
            Proposal(new RunId(runId), "read", "failure-1"),
            CancellationToken.None);
        var second = gateway.EvaluateAsync(
            Proposal(new RunId(runId), "read", "failure-2"),
            CancellationToken.None);

        var failures = await Task.WhenAll(
                Assert.ThrowsAsync<ToolTransportException>(() => first),
                Assert.ThrowsAsync<ToolTransportException>(() => second))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(failures, failure => Assert.Equal("broker_connection_closed", failure.Message));
    }

    [Fact]
    public async Task Failed_startup_does_not_invalidate_a_newer_connection()
    {
        await using var broker = await DelayedWebSocketBroker.StartAsync(
            failFirstRunOpen: true,
            holdSecondEvaluate: true);
        var runId = "run_" + new string('d', 31) + "4";
        const string sessionId = "session-startup-race";
        await using var gateway = new global::RuBackend.Harness.Broker.BrokerToolGateway(
            broker.Uri,
            broker.Token,
            sessionId,
            runId,
            () => BuildRunOpenPayload(
                runId,
                sessionId,
                Path.GetTempPath(),
                "plan"));
        var setupFailureResumed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ConnectionSetupFailureAsync = async cancellationToken =>
        {
            await broker.SecondEvaluateReceived.WaitAsync(cancellationToken);
            setupFailureResumed.TrySetResult(true);
        };

        var first = gateway.EvaluateAsync(
            Proposal(new RunId(runId), "read", "startup-1"),
            CancellationToken.None);
        await broker.FirstRunOpenFailed.WaitAsync(TimeSpan.FromSeconds(5));
        var second = gateway.EvaluateAsync(
            Proposal(new RunId(runId), "read", "startup-2"),
            CancellationToken.None);
        await setupFailureResumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        broker.ReleaseResponses();

        var decisions = await Task.WhenAll(first, second)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(decisions, decision => Assert.Equal(PolicyDecision.Allow, decision.Decision));
    }

    private static ToolProposal Proposal(
        RunId runId,
        string toolId,
        string providerCallId) =>
        new(
            runId,
            new ModelStepId("step_" + new string('0', 32)),
            new ToolCallId("call_" + new string('0', 31) + (providerCallId.EndsWith('1') ? '1' : '2')),
            providerCallId,
            1,
            toolId,
            "1.0.0",
            JsonDocument.Parse("{}").RootElement.Clone());

    protected override bool StreamsToolOutputDeltas => false;

    protected override async Task<IToolGateway> CreateGatewayAsync(string workspaceRoot, string preset)
    {
        var host = await BrokerTestHost.StartAsync(workspaceRoot);
        _hosts.Add(host);
        const string sessionId = "session-conformance";
        var runId = "run_conf" + Guid.NewGuid().ToString("N")[..28];
        return new RuBackend.Harness.Broker.BrokerToolGateway(
            new Uri(host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            host.Token,
            sessionId,
            runId,
            () => BuildRunOpenPayload(runId, sessionId, workspaceRoot, preset));
    }

    protected override void DisposeGateway()
    {
        foreach (var host in _hosts)
        {
            try { host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
        }

        _hosts.Clear();
    }

    private static JsonObject BuildRunOpenPayload(string runId, string sessionId, string canonicalRoot, string preset)
    {
        var snapshot = new BrokerPolicySnapshotWire(
            1,
            preset,
            new BrokerPolicyWorkspaceIdentityWire(
                "ws_11111111111111111111111111111111",
                "VOL-CONFORMANCE",
                "fid-conformance",
                new string('a', 64)),
            [],
            "2026-08-24T12:00:00.0000000+00:00");
        return new JsonObject
        {
            ["run_id"] = runId,
            ["session_id"] = sessionId,
            ["workspace_id"] = snapshot.Workspace.WorkspaceId,
            ["canonical_root"] = canonicalRoot,
            ["volume_serial"] = snapshot.Workspace.VolumeSerialNumber,
            ["root_file_id"] = snapshot.Workspace.RootFileId,
            ["display_hash"] = snapshot.Workspace.DisplayIdentityHash,
            ["preset"] = snapshot.Preset,
            ["snapshot_version"] = snapshot.Version,
            ["snapshot_hash"] = BrokerPolicyContracts.ComputeCanonicalSnapshotHash(snapshot),
            ["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"),
            ["snapshot"] = BrokerPolicyContracts.WriteSnapshot(snapshot),
        };
    }

    private sealed class DelayedWebSocketBroker : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly bool _closeAfterEvaluations;
        private readonly bool _failFirstRunOpen;
        private readonly bool _holdSecondEvaluate;
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _firstRunOpenFailed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _secondEvaluateReceived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _secondResponseSent =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _responseOrder = [];
        private readonly object _responseOrderSync = new();
        private int _connectionCount;
        private int _evaluateCount;
        private int _activeResponses;
        private int _maximumConcurrentResponses;

        private DelayedWebSocketBroker(
            WebApplication application,
            bool closeAfterEvaluations,
            bool failFirstRunOpen,
            bool holdSecondEvaluate)
        {
            _application = application;
            _closeAfterEvaluations = closeAfterEvaluations;
            _failFirstRunOpen = failFirstRunOpen;
            _holdSecondEvaluate = holdSecondEvaluate;
        }

        public Uri Uri { get; private set; } = null!;

        public string Token { get; } = "token-concurrency";

        public int MaximumConcurrentResponses => Volatile.Read(ref _maximumConcurrentResponses);

        public Task FirstRunOpenFailed => _firstRunOpenFailed.Task;

        public Task SecondEvaluateReceived => _secondEvaluateReceived.Task;

        public IReadOnlyList<string> ResponseOrder
        {
            get
            {
                lock (_responseOrderSync)
                {
                    return _responseOrder.ToArray();
                }
            }
        }

        public void ReleaseResponses() => _release.TrySetResult(true);

        public static async Task<DelayedWebSocketBroker> StartAsync(
            bool closeAfterEvaluations = false,
            bool failFirstRunOpen = false,
            bool holdSecondEvaluate = false)
        {
            var port = GetAvailablePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(BrokerToolGatewayConformanceTests).Assembly.GetName().Name,
                Args = [],
            });
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            var application = builder.Build();
            var broker = new DelayedWebSocketBroker(
                application,
                closeAfterEvaluations,
                failFirstRunOpen,
                holdSecondEvaluate);
            application.UseWebSockets();
            application.Map("/agent", broker.HandleAsync);
            await application.StartAsync();
            broker.Uri = new Uri($"ws://127.0.0.1:{port}/agent");
            return broker;
        }

        public async ValueTask DisposeAsync()
        {
            _release.TrySetResult(true);
            await _application.StopAsync();
            await _application.DisposeAsync();
        }

        private async Task HandleAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var sendGate = new SemaphoreSlim(1, 1);
            var responseTasks = new List<Task>();
            var buffer = new byte[16 * 1024];
            var connectionNumber = Interlocked.Increment(ref _connectionCount);
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var request = await ReceiveAsync(socket, buffer);
                    if (request is null)
                    {
                        break;
                    }

                    if (request.MessageType == AgentMessageTypes.RunOpen)
                    {
                        if (_failFirstRunOpen && connectionNumber == 1)
                        {
                            _firstRunOpenFailed.TrySetResult(true);
                            await socket.CloseAsync(
                                    WebSocketCloseStatus.EndpointUnavailable,
                                    "startup_failure",
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                            return;
                        }

                        await SendAsync(
                                socket,
                                sendGate,
                                new AgentEnvelope(
                                    AgentEnvelopeCodec.ProtocolVersionV2,
                                    request.RequestId,
                                    AgentMessageTypes.RunOpened,
                                    request.SessionId,
                                    request.CorrelationId,
                                    request.DeadlineMs,
                                    new JsonObject
                                    {
                                        ["status"] = ToolResultStatuses.Succeeded,
                                        ["run_id"] = request.Payload["run_id"]!.GetValue<string>(),
                                        ["snapshot_version"] = 1,
                                    }),
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (request.MessageType == AgentMessageTypes.ToolEvaluate)
                    {
                        var toolCallId = request.Payload["tool_call_id"]!.GetValue<string>();
                        var waitForSecondResponse = false;
                        if (_holdSecondEvaluate && connectionNumber == 2)
                        {
                            _secondEvaluateReceived.TrySetResult(true);
                            await _release.Task.ConfigureAwait(false);
                        }
                        else if (!_holdSecondEvaluate)
                        {
                            var evaluateNumber = Interlocked.Increment(ref _evaluateCount);
                            if (evaluateNumber == 2)
                            {
                                if (_closeAfterEvaluations)
                                {
                                    _secondResponseSent.TrySetResult(true);
                                    await socket.CloseAsync(
                                            WebSocketCloseStatus.EndpointUnavailable,
                                            "test_close",
                                            CancellationToken.None)
                                        .ConfigureAwait(false);
                                    return;
                                }

                                _release.TrySetResult(true);
                            }

                            waitForSecondResponse = toolCallId.EndsWith(
                                "1",
                                StringComparison.Ordinal);
                        }

                        responseTasks.Add(SendDecisionAsync(
                            socket,
                            sendGate,
                            request,
                            waitForSecondResponse));
                    }
                }
            }
            finally
            {
                _release.TrySetResult(true);
                try
                {
                    await Task.WhenAll(responseTasks).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        private async Task SendDecisionAsync(
            WebSocket socket,
            SemaphoreSlim sendGate,
            AgentEnvelope request,
            bool waitForSecondResponse)
        {
            var active = Interlocked.Increment(ref _activeResponses);
            var maximum = Volatile.Read(ref _maximumConcurrentResponses);
            while (active > maximum)
            {
                var observed = Interlocked.CompareExchange(
                    ref _maximumConcurrentResponses,
                    active,
                    maximum);
                if (observed == maximum)
                {
                    break;
                }

                maximum = observed;
            }

            try
            {
                await _release.Task.ConfigureAwait(false);
                if (waitForSecondResponse)
                {
                    await _secondResponseSent.Task.ConfigureAwait(false);
                }

                await Task.Delay(50);
                var toolCallId = request.Payload["tool_call_id"]!.GetValue<string>();
                await SendAsync(
                        socket,
                        sendGate,
                        new AgentEnvelope(
                            AgentEnvelopeCodec.ProtocolVersionV2,
                            request.RequestId,
                            AgentMessageTypes.ToolDecision,
                            request.SessionId,
                            request.CorrelationId,
                            request.DeadlineMs,
                            new JsonObject
                            {
                                ["status"] = ToolResultStatuses.Succeeded,
                                ["decision"] = "allow",
                                ["authorization_token"] = "auth-" + toolCallId,
                            }),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                lock (_responseOrderSync)
                {
                    _responseOrder.Add(toolCallId);
                }
            }
            finally
            {
                if (!waitForSecondResponse)
                {
                    _secondResponseSent.TrySetResult(true);
                }

                Interlocked.Decrement(ref _activeResponses);
            }
        }

        private static async Task<AgentEnvelope?> ReceiveAsync(
            WebSocket socket,
            byte[] buffer)
        {
            using var content = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                content.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                return AgentEnvelopeCodec.Parse(Encoding.UTF8.GetString(content.ToArray()));
            }
        }

        private static async Task SendAsync(
            WebSocket socket,
            SemaphoreSlim sendGate,
            AgentEnvelope envelope,
            CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(AgentEnvelopeCodec.Serialize(envelope));
            await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                sendGate.Release();
            }
        }

        private static int GetAvailablePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed class BrokerTestHost : IAsyncDisposable
    {
        private BrokerTestHost(string baseUrl, string token, BrokerTestHostHost host, string? previousWorkspaceRoot)
        {
            BaseUrl = baseUrl;
            Token = token;
            Host = host;
            PreviousWorkspaceRoot = previousWorkspaceRoot;
        }

        public string BaseUrl { get; }

        public string Token { get; }

        private BrokerTestHostHost Host { get; }

        private string? PreviousWorkspaceRoot { get; }

        public static async Task<BrokerTestHost> StartAsync(string workspaceRoot)
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-broker-conformance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sessionRoot = Path.Combine(root, "session");
            Directory.CreateDirectory(sessionRoot);
            var token = "broker-conformance-" + Guid.NewGuid().ToString("N");
            var descriptorPath = Path.Combine(root, "broker.json");
            var descriptor = new BrokerSessionDescriptor(
                "http://127.0.0.1:" + GetFreePort(),
                token,
                1,
                sessionRoot,
                DateTimeOffset.UtcNow.AddMinutes(30));
            File.WriteAllText(
                descriptorPath,
                JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));

            var previousWorkspaceRoot = Environment.GetEnvironmentVariable("RU_WORKSPACE_ROOT");
            Environment.SetEnvironmentVariable("RU_WORKSPACE_ROOT", workspaceRoot);
            var host = await BrokerTestHostHost.StartAsync(descriptorPath);
            return new BrokerTestHost(descriptor.BaseUrl, token, host, previousWorkspaceRoot);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Environment.SetEnvironmentVariable("RU_WORKSPACE_ROOT", PreviousWorkspaceRoot);
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
