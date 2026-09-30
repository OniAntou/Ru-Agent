using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Rpc;

/// <summary>
/// Owns run event subscriptions: one SSE pump per attachment, durable frames
/// published as run.event and (opt-in) transient frames as run.transient. The
/// publish callback returns false when the outbound queue is full; the pump
/// then stops instead of dropping a durable event silently.
/// </summary>
public sealed class RpcRunBridge : IRunBridge, IDisposable
{
    private const int MaxStreamResumes = 3;
    internal const string DetachReasonStreamInterrupted = "stream_interrupted";
    internal const string DetachReasonSubscriptionEnded = "subscription_ended";
    private static readonly TimeSpan StreamResumeDelay = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions EnvelopeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly AgentSseClient _sseClient;
    private readonly AgentApiClient _agentClient;
    private readonly Func<JsonObject, bool> _publish;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();
    private readonly object _gate = new();
    private int _nextSubscriptionId;
    private bool _disposed;

    public RpcRunBridge(
        AgentSseClient sseClient,
        AgentApiClient agentClient,
        Func<JsonObject, bool> publish,
        Action<string>? log = null)
    {
        _sseClient = sseClient ?? throw new ArgumentNullException(nameof(sseClient));
        _agentClient = agentClient ?? throw new ArgumentNullException(nameof(agentClient));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _log = log ?? (_ => { });
    }

    public string Attach(string runId, long afterSequence, bool includeTransient)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var subscriptionId = $"sub_{Interlocked.Increment(ref _nextSubscriptionId)}";
            var cts = new CancellationTokenSource();
            var subscription = new Subscription(subscriptionId, runId, includeTransient, cts);
            if (!_subscriptions.TryAdd(subscriptionId, subscription))
            {
                cts.Dispose();
                throw new RpcDispatchException(RpcProtocol.InternalError, "subscription id collision.");
            }

            subscription.Pump = Task.Run(() => PumpAsync(subscription, afterSequence), CancellationToken.None);
            return subscriptionId;
        }
    }

    public bool Detach(string subscriptionId)
    {
        if (!_subscriptions.TryRemove(subscriptionId, out var subscription))
        {
            return false;
        }

        subscription.Cancel();
        return true;
    }

    /// <summary>Waits until every active subscription pump has finished (tests and shutdown).</summary>
    public async Task WaitForSubscriptionsAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!_subscriptions.IsEmpty && DateTimeOffset.UtcNow < deadline)
        {
            var pumps = _subscriptions.Values.Select(s => s.Pump).Where(t => t is not null).ToArray();
            if (pumps.Length == 0)
            {
                break;
            }

            await Task.WhenAny(Task.WhenAll(pumps!), Task.Delay(50)).ConfigureAwait(false);
        }
    }

    private async Task PumpAsync(Subscription subscription, long afterSequence)
    {
        var resumes = 0;
        string? detachReason = null;
        try
        {
            while (true)
            {
                subscription.Token.ThrowIfCancellationRequested();
                try
                {
                    await foreach (var frame in _sseClient.ReadEventsAsync(subscription.RunId, afterSequence, subscription.Token).ConfigureAwait(false))
                    {
                        subscription.Token.ThrowIfCancellationRequested();
                        if (frame.DurableSequence is { } seq && seq > afterSequence)
                        {
                            afterSequence = seq;
                        }

                        resumes = 0;
                        if (!PublishFrame(subscription, frame))
                        {
                            // The outbound queue is full: the bridge is already
                            // failing closed with bridge.shutdown, so there is no
                            // room (and no point) in signalling this subscription.
                            return;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException && !subscription.Token.IsCancellationRequested)
                {
                    resumes++;
                    if (resumes > MaxStreamResumes)
                    {
                        detachReason = DetachReasonStreamInterrupted;
                        return;
                    }

                    await Task.Delay(StreamResumeDelay, subscription.Token).ConfigureAwait(false);
                    continue;
                }

                var terminal = await IsRunTerminalAsync(subscription.RunId, subscription.Token).ConfigureAwait(false);
                if (terminal == true)
                {
                    return;
                }

                if (terminal is null || resumes >= MaxStreamResumes)
                {
                    // The stream ended and the run is not known terminal: end the
                    // subscription loudly instead of leaving consumers waiting.
                    detachReason = DetachReasonSubscriptionEnded;
                    return;
                }

                resumes++;
                await Task.Delay(StreamResumeDelay, subscription.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (AgentApiException ex)
        {
            // A dead subscription must not fault the pump task into the void: the
            // failure must be visible on stderr and on the wire.
            detachReason = DetachReasonStreamInterrupted;
            _log($"rpc run subscription ended: {ex.Message}");
        }
        catch (Exception ex)
        {
            detachReason = DetachReasonStreamInterrupted;
            _log($"rpc run subscription ended: {ex.Message}");
        }
        finally
        {
            PublishDetached(subscription, detachReason);
            _subscriptions.TryRemove(subscription.SubscriptionId, out _);
            subscription.Cancellation.Dispose();
        }
    }

    /// <summary>
    /// Announces a pump that ended before the run reached a terminal state. An
    /// explicitly cancelled subscription (detach or shutdown) is not announced.
    /// </summary>
    private void PublishDetached(Subscription subscription, string? reason)
    {
        if (reason is null || subscription.Token.IsCancellationRequested)
        {
            return;
        }

        _publish(RpcMessage.Notification("run.detached", new JsonObject
        {
            ["subscription_id"] = subscription.SubscriptionId,
            ["reason"] = reason,
        }));
    }

    private bool PublishFrame(Subscription subscription, SseFrame frame)
    {
        if (frame.IsTransient)
        {
            if (!subscription.IncludeTransient)
            {
                return true;
            }

            var transient = new JsonObject
            {
                ["subscription_id"] = subscription.SubscriptionId,
                ["transient_sequence"] = frame.TransientSequence,
                ["event_type"] = frame.EventType,
                ["data"] = frame.Data.TrimEnd('\r'),
            };
            return _publish(RpcMessage.Notification("run.transient", transient));
        }

        if (frame.DurableSequence is null)
        {
            return true;
        }

        HarnessSafeEventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data, EnvelopeOptions);
        }
        catch (JsonException)
        {
            return true;
        }

        if (envelope is null || envelope.ProtocolVersion != HarnessSafeEventEnvelope.CurrentProtocolVersion)
        {
            return true;
        }

        var parameters = new JsonObject
        {
            ["subscription_id"] = subscription.SubscriptionId,
            ["protocol_version"] = envelope.ProtocolVersion,
            ["event_id"] = envelope.EventId,
            ["run_id"] = envelope.RunId,
            ["session_id"] = envelope.SessionId,
            ["sequence"] = envelope.Sequence,
            ["event_type"] = envelope.EventType,
            ["occurred_at"] = envelope.OccurredAt,
            ["status"] = envelope.Status,
            ["safe_summary"] = envelope.SafeSummary,
            ["payload_ref"] = envelope.PayloadRef,
            ["input_tokens"] = envelope.InputTokens,
            ["output_tokens"] = envelope.OutputTokens,
            ["total_tokens"] = envelope.TotalTokens,
        };
        if (NdjsonEventRenderer.ContainsForbidden(parameters.ToJsonString()))
        {
            return true;
        }

        return _publish(RpcMessage.Notification("run.event", parameters));
    }

    private async Task<bool?> IsRunTerminalAsync(string runId, CancellationToken ct)
    {
        try
        {
            var run = await _agentClient.GetRunAsync(runId, ct).ConfigureAwait(false);
            return run.TerminalAt is not null
                || string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(run.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(run.Status, "interrupted", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        Subscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            subscriptions = _subscriptions.Values.ToArray();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Cancel();
        }
    }

    private sealed class Subscription(string subscriptionId, string runId, bool includeTransient, CancellationTokenSource cancellation)
    {
        public string SubscriptionId { get; } = subscriptionId;
        public string RunId { get; } = runId;
        public bool IncludeTransient { get; } = includeTransient;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public CancellationToken Token => Cancellation.Token;
        public Task? Pump { get; set; }

        public void Cancel()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
