using System.Net.WebSockets;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Protocol;

namespace RuBackend.Harness.Broker;

public sealed partial class BrokerToolGateway : IToolGateway, IToolRunCancellation, IApprovalCleanupGateway, IAsyncDisposable
{
    internal const string AuthorizationTokenPrefix = "auth:";

    private readonly BrokerApprovalState? _approvals;
    private readonly object _bindingSync = new();
    private GatewayBinding _binding;
    private BrokerRunLeaseIdentity? _currentLease;
    private GatewayGeneration? _currentGeneration;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly Dictionary<string, PendingExecution> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingReply> _pendingReplies = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cancelled = new(StringComparer.Ordinal);
    private ClientWebSocket? _socket;
    private Task? _receiveLoop;
    private CancellationTokenSource? _receiveCancellation;
    private long _requestSequence;
    private long _connectionGeneration;
    private readonly object _rpcSync = new();
    private int _activeRpcCount;
    private TaskCompletionSource<bool> _rpcsDrained = CompletedRpcSignal();
    private int _disposed;

    private sealed record GatewayBinding(
        Uri BrokerUrl,
        string Bearer,
        string SessionId,
        string RunId,
        Func<JsonObject> RunOpenPayloadFactory,
        BrokerRunLeaseIdentity? Lease);

    private sealed record GatewayGeneration(
        BrokerRunLeaseIdentity? Lease,
        long Value);

    private sealed class ExecutionDispatchState
    {
        public bool IsDispatched { get; private set; }

        public void MarkDispatched() => IsDispatched = true;
    }

    private sealed record PendingExecution(
        GatewayGeneration Generation,
        string ToolId,
        string ToolVersion,
        ToolAccessKind AccessKind,
        string AuthorizationToken,
        string ArgumentsJson);

    private sealed record PendingReply(
        TaskCompletionSource<AgentEnvelope> Completion,
        TaskCompletionSource<bool> SendHook,
        Func<string, string>? ReplyTextTransform);

    private readonly struct ActiveRpcScope : IDisposable
    {
        private readonly BrokerToolGateway _owner;

        public ActiveRpcScope(BrokerToolGateway owner)
        {
            _owner = owner;
        }

        public void Dispose() => _owner.ExitRpc();
    }

    public BrokerToolGateway(
        Uri brokerUrl,
        string bearer,
        string sessionId,
        string runId,
        Func<JsonObject> runOpenPayloadFactory,
        BrokerApprovalState? approvals = null,
        BrokerRunLeaseIdentity? lease = null)
    {
        ArgumentNullException.ThrowIfNull(brokerUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearer);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(runOpenPayloadFactory);

        _approvals = approvals;
        if (approvals is not null && lease is null)
        {
            throw new ArgumentNullException(nameof(lease));
        }

        _binding = new GatewayBinding(
            brokerUrl,
            bearer,
            sessionId,
            runId,
            runOpenPayloadFactory,
            lease);
    }

    public BrokerRunLeaseIdentity? Lease
    {
        get
        {
            lock (_bindingSync)
            {
                return _currentLease;
            }
        }
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal Func<BrokerRunLeaseIdentity?, CancellationToken, Task>? BeforeExecutionSendAsync { get; set; }

    internal Func<CancellationToken, Task>? AfterExecutionSendAsync { get; set; }

    internal Func<string, string>? ExecutionReplyTextTransform { get; set; }

    internal Func<CancellationToken, Task>? ConnectionSetupFailureAsync { get; set; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task? receiveLoop;
        CancellationTokenSource? receiveCancellation;
        TaskCompletionSource<bool> rpcsDrained;
        lock (_bindingSync)
        {
            receiveLoop = _receiveLoop;
            receiveCancellation = _receiveCancellation;
            _receiveLoop = null;
            _receiveCancellation = null;
        }

        lock (_rpcSync)
        {
            rpcsDrained = _rpcsDrained;
        }

        InvalidateConnection();
        receiveCancellation?.Cancel();
        try
        {
            await Task.WhenAll(
                    rpcsDrained.Task,
                    receiveLoop ?? Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        receiveCancellation?.Dispose();
    }
}
