using System.Net.Http;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuApp.Services;

public sealed partial class AgentRunClientAdapter : IAgentRunClient, IDisposable
{
    private readonly AgentApiClient _apiClient;
    private readonly Func<AgentSseClient> _sseFactory;
    private readonly DispatcherQueue? _dispatcher;

    public AgentRunClientAdapter(
        PublicHostSession session,
        HttpClient? httpClient = null,
        DispatcherQueue? dispatcher = null,
        HttpClient? sseHttpClient = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _apiClient = new AgentApiClient(session, httpClient);
        // A caller may share one client for both transports (tests do), or hand
        // the SSE side its own client with an infinite timeout so a long stream
        // is not cut off by the API request timeout.
        var sseClient = sseHttpClient ?? httpClient;
        _sseFactory = () => new AgentSseClient(session, sseClient);
        _dispatcher = dispatcher;
    }

    public AgentRunClientAdapter(AgentApiClient apiClient, Func<AgentSseClient> sseFactory, DispatcherQueue? dispatcher = null)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _sseFactory = sseFactory ?? throw new ArgumentNullException(nameof(sseFactory));
        _dispatcher = dispatcher;
    }

    public async Task<AgentRunModel> CreateRunAsync(CreateAgentRunRequest request, CancellationToken cancellationToken = default)
        => AgentRunModel.FromCreateResponse(await _apiClient.CreateRunAsync(request, cancellationToken).ConfigureAwait(false));

    public async Task<AgentRunModel> GetRunAsync(string runId, CancellationToken cancellationToken = default)
        => AgentRunModel.FromGetResponse(await _apiClient.GetRunAsync(runId, cancellationToken).ConfigureAwait(false));

    public async Task<AgentPayloadModel> GetPayloadAsync(string runId, string payloadId, CancellationToken cancellationToken = default)
    {
        var content = await _apiClient.GetPayloadAsync(runId, payloadId, cancellationToken).ConfigureAwait(false);
        return AgentPayloadModel.FromContent(runId, payloadId, content);
    }
    public async Task TrustAsync(
        string runId,
        TrustAgentRunRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = await _apiClient.TrustAsync(runId, request, cancellationToken).ConfigureAwait(false);
    }
    public async Task<long> SetSessionPresetAsync(
        string sessionId,
        string preset,
        long expectedVersion,
        bool interactive,
        CancellationToken cancellationToken = default)
    {
        var response = await _apiClient.SetSessionPresetAsync(
            sessionId,
            preset,
            expectedVersion,
            interactive,
            cancellationToken).ConfigureAwait(false);
        if (!response.TryGetProperty("version", out var versionProperty)
            || !versionProperty.TryGetInt64(out var version)
            || version <= 0)
        {
            throw new AgentApiException("invalid_response", "Preset response did not contain a valid version.");
        }

        return version;
    }

    public async Task ApproveAsync(
        string runId,
        string approvalId,
        string scope,
        CancellationToken cancellationToken = default)
    {
        _ = await _apiClient.ApproveAsync(runId, approvalId, scope, cancellationToken).ConfigureAwait(false);
    }

    public async Task DenyAsync(
        string runId,
        string approvalId,
        CancellationToken cancellationToken = default)
    {
        _ = await _apiClient.DenyAsync(runId, approvalId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a pending Host-intercepted question with the user's answer,
    /// through the same shared client method the CLI answers with.
    /// </summary>
    public async Task SubmitInputAsync(
        string runId,
        string inputId,
        string answer,
        CancellationToken cancellationToken = default)
    {
        _ = await _apiClient.AnswerAsync(runId, inputId, answer, cancellationToken).ConfigureAwait(false);
    }
    public Task CancelAsync(string runId, CancellationToken cancellationToken = default)
        => _apiClient.CancelAsync(runId, cancellationToken);

    public Task<DispatchAgentCommandResponse> DispatchCommandAsync(
        string runId,
        string text,
        CancellationToken cancellationToken = default)
        => _apiClient.DispatchCommandAsync(runId, text, cancellationToken);

    public Task ContinueAsync(string runId, CancellationToken cancellationToken = default)
        => _apiClient.ContinueAsync(runId, cancellationToken);


    /// <summary>
    /// Streams run events, transparently resuming from the last durable sequence
    /// when the transport breaks mid-stream (bounded retries), mirroring the CLI
    /// tracer. A clean stream end only completes when the run itself is terminal;
    /// an inconclusive status check (backend unreachable) ends the subscription
    /// the way it always did instead of spinning.
    /// </summary>
    public async Task SubscribeEventsAsync(
        string runId,
        long afterSequence,
        Func<AgentEventModel, Task> onEvent,
        CancellationToken cancellationToken = default,
        Func<AgentActivityModel, Task>? onActivity = null,
        Func<AgentRunSummaryModel, Task>? onSummary = null,
        Func<AgentAssistantDeltaModel, Task>? onAssistantDelta = null,
        Func<AgentToolOutputModel, Task>? onToolOutput = null)
    {
        ArgumentNullException.ThrowIfNull(onEvent);

        await AgentRunFeed.FollowAsync(
                afterSequence,
                (cursor, token) => ReadEventsAsync(runId, cursor, token),
                token => IsRunTerminalAsync(runId, token),
                (frame, token) => DispatchFrameAsync(
                    frame,
                    onEvent,
                    onActivity,
                    onSummary,
                    onAssistantDelta,
                    onToolOutput,
                    token),
                cancellationToken,
                new AgentRunFeedOptions
                {
                    IncludeSocketExceptions = true,
                    TransportFailureFactory = (failure, _) => new AgentApiException(
                        "stream_interrupted",
                        $"event stream broke: {failure.Message}",
                        inner: failure),
                })
            .ConfigureAwait(false);
    }

    private async IAsyncEnumerable<SseFrame> ReadEventsAsync(
        string runId,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var sse = _sseFactory();
        await foreach (var frame in sse.ReadEventsAsync(runId, afterSequence, cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    private async Task DispatchFrameAsync(
        SseFrame frame,
        Func<AgentEventModel, Task> onEvent,
        Func<AgentActivityModel, Task>? onActivity,
        Func<AgentRunSummaryModel, Task>? onSummary,
        Func<AgentAssistantDeltaModel, Task>? onAssistantDelta,
        Func<AgentToolOutputModel, Task>? onToolOutput,
        CancellationToken cancellationToken)
    {
        if (frame.IsTransient)
        {
            if (onAssistantDelta is not null
                && AgentAssistantDeltaModel.TryParse(frame, out var assistantDelta)
                && assistantDelta is not null)
            {
                await DispatchAsync(() => onAssistantDelta(assistantDelta), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (onSummary is not null
                && AgentRunSummaryModel.TryParse(frame, out var summary)
                && summary is not null)
            {
                await DispatchAsync(() => onSummary(summary), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (onToolOutput is not null
                && AgentToolOutputModel.TryParse(frame, out var toolOutput)
                && toolOutput is not null)
            {
                await DispatchAsync(() => onToolOutput(toolOutput), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (onActivity is null
                || !AgentActivityModel.TryParse(frame, out var activity)
                || activity is null)
            {
                // Raw assistant/tool deltas and malformed activity frames are not
                // durable events and must never reach the audit/status collection.
                return;
            }

            await DispatchAsync(() => onActivity(activity), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        AgentEventModel model;
        try
        {
            model = AgentEventModel.FromSseFrame(frame);
        }
        catch (InvalidOperationException)
        {
            // One malformed durable frame must not kill the whole subscription:
            // the cursor already advanced past it, so the rest of the stream
            // (including the terminal snapshot) still arrives.
            return;
        }

        await DispatchAsync(() => onEvent(model), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <returns>
    /// True when the run is provably terminal, false when provably still active,
    /// null when the status check itself failed (inconclusive).
    /// </returns>
    private async Task<bool?> IsRunTerminalAsync(string runId, CancellationToken cancellationToken)
    {
        var run = await _apiClient.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        return run.Status is "completed" or "failed" or "cancelled" or "interrupted";
    }

    private async Task DispatchAsync(Func<Task> callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (_dispatcher is null)
        {
            await callback().ConfigureAwait(false);
            return;
        }

        var delivered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued;
        try
        {
            queued = _dispatcher.TryEnqueue(async () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    delivered.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    await callback().ConfigureAwait(true);
                    delivered.TrySetResult(null);
                }
                catch (OperationCanceledException error)
                {
                    delivered.TrySetException(error);
                }
                catch (Exception error)
                {
                    delivered.TrySetException(error);
                }
            });
        }
        catch (InvalidOperationException)
        {
            queued = false;
        }

        if (queued)
        {
            await delivered.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // A dispatcher can disappear during window shutdown. Do not silently lose
            // a durable event or activity; deliver on the caller context instead.
            await callback().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_apiClient is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
