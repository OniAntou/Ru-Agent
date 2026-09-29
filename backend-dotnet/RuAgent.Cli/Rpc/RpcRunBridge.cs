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
    private static readonly TimeSpan StreamResumeDelay = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions EnvelopeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly AgentSseClient _sseClient;
    private readonly AgentApiClient _agentClient;
    private readonly Func<JsonObject, bool> _publish;
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();
    private int _nextSubscriptionId;

    public RpcRunBridge(AgentSseClient sseClient, AgentApiClient agentClient, Func<JsonObject, bool> publish)
    {
        _sseClient = sseClient ?? throw new ArgumentNullException(nameof(sseClient));
        _agentClient = agentClient ?? throw new ArgumentNullException(nameof(agentClient));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
    }

    public string Attach(string runId, long afterSequence, bool includeTransient)
    {
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

            await Task.WhenAll(pumps!).WaitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
    }

    private async Task PumpAsync(Subscription subscription, long afterSequence)
    {
        var resumes = 0;
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
                            return;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException && !subscription.Token.IsCancellationRequested)
                {
                    resumes++;
                    if (resumes > MaxStreamResumes)
                    {
                        return;
                    }

                    await Task.Delay(StreamResumeDelay, subscription.Token).ConfigureAwait(false);
                    continue;
                }

                if (await IsRunTerminalAsync(subscription.RunId, subscription.Token).ConfigureAwait(false) != false)
                {
                    return;
                }

                resumes++;
                if (resumes > MaxStreamResumes)
                {
                    return;
                }

                await Task.Delay(StreamResumeDelay, subscription.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _subscriptions.TryRemove(subscription.SubscriptionId, out _);
        }
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
        foreach (var subscription in _subscriptions.Values)
        {
            subscription.Cancel();
            subscription.Cancellation.Dispose();
        }

        _subscriptions.Clear();
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
