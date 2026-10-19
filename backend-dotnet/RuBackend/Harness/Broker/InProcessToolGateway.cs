using System.Text.Json.Nodes;
using RuAgent.Broker.Runtime;
using RuAgent.Harness.Core;
using RuAgent.Protocol;

namespace RuBackend.Harness.Broker;

/// <summary>
/// The in-process <see cref="IToolGateway"/>: one gateway per run lease, bound
/// to the transport-neutral <see cref="BrokerExecutionRuntime"/>. Decisions and
/// receipts are projected through the <see cref="BrokerGatewayContracts"/>
/// helpers so identical inputs produce identical model-visible results.
/// </summary>
public sealed partial class InProcessToolGateway : IToolGateway, IToolRunCancellation, IApprovalCleanupGateway, IAsyncDisposable
{
    private readonly BrokerExecutionRuntime _runtime;
    private readonly BrokerApprovalState? _approvals;
    private readonly string _runId;
    private readonly Lazy<Subagents.SubagentControlDispatcher>? _subagents;
    private readonly object _sync = new();
    private readonly Dictionary<string, PendingExecution> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cancelled = new(StringComparer.Ordinal);
    private BrokerRunLeaseIdentity _lease;
    private long _generation;
    private long _requestSequence;
    private int _disposed;

    private sealed record PendingExecution(
        long Generation,
        string ToolId,
        string ToolVersion,
        ToolAccessKind AccessKind,
        string AuthorizationToken,
        string ArgumentsJson,
        ToolProposal Proposal);

    public InProcessToolGateway(
        BrokerExecutionRuntime runtime,
        BrokerRunLeaseIdentity lease,
        BrokerApprovalState? approvals = null,
        Lazy<Subagents.SubagentControlDispatcher>? subagents = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        if (lease.Generation <= 0)
        {
            throw new ArgumentException(
                "The in-process lease identity must carry a positive gateway generation.",
                nameof(lease));
        }

        _approvals = approvals;
        _subagents = subagents;
        _runId = lease.RunId;
        _generation = lease.Generation;
    }

    /// <summary>The run lease identity this gateway currently executes under.</summary>
    public BrokerRunLeaseIdentity Lease
    {
        get
        {
            lock (_sync)
            {
                return _lease;
            }
        }
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Moves this gateway to a new run lease (same run, new policy snapshot or
    /// session) after the factory re-opened the lease through the runtime.
    /// Pending preparations and queued cancellations from the old generation are
    /// dropped, exactly like a wire reconnect. The session id travels with the
    /// lease, so every runtime call after a rebind addresses the new session.
    /// </summary>
    internal void Rebind(BrokerRunLeaseIdentity lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!string.Equals(lease.RunId, _runId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The lease does not belong to this gateway's run.", nameof(lease));
        }

        if (lease.Generation <= 0)
        {
            throw new ArgumentException("The lease generation must be positive.", nameof(lease));
        }

        lock (_sync)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(InProcessToolGateway));
            }

            _lease = lease;
            _generation = lease.Generation;
            _pending.Clear();
            _cancelled.Clear();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        lock (_sync)
        {
            _pending.Clear();
            _cancelled.Clear();
        }

        // Disposing a gateway must not leave its children running: the run
        // cancellation registry is signalled so an in-flight terminal
        // execution terminates its process tree. Best effort: teardown never
        // throws.
        try
        {
            _runtime.CancelRun(_runId);
        }
        catch (Exception)
        {
        }

        return ValueTask.CompletedTask;
    }

    private AgentEnvelope Request(string sessionId, string messageType, JsonObject payload) =>
        new(
            AgentEnvelopeCodec.ProtocolVersionV2,
            "inproc-" + Interlocked.Increment(ref _requestSequence),
            messageType,
            sessionId,
            null,
            10_000,
            payload);
}
