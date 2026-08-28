using System.Net.Http;
using Microsoft.UI.Dispatching;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuApp.Services;

public sealed class AgentRunClientAdapter : IAgentRunClient, IDisposable
{
    private readonly AgentApiClient _apiClient;
    private readonly Func<AgentSseClient> _sseFactory;
    private readonly DispatcherQueue? _dispatcher;

    public AgentRunClientAdapter(PublicHostSession session, HttpClient? httpClient = null, DispatcherQueue? dispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _apiClient = new AgentApiClient(session, httpClient);
        _sseFactory = () => new AgentSseClient(session, httpClient);
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
    public Task CancelAsync(string runId, CancellationToken cancellationToken = default)
        => _apiClient.CancelAsync(runId, cancellationToken);

    public Task ContinueAsync(string runId, CancellationToken cancellationToken = default)
        => _apiClient.ContinueAsync(runId, cancellationToken);


    public async Task SubscribeEventsAsync(
        string runId,
        long afterSequence,
        Func<AgentEventModel, Task> onEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        using var sse = _sseFactory();
        await foreach (var frame in sse.ReadEventsAsync(runId, afterSequence, cancellationToken).ConfigureAwait(false))
        {
            var model = AgentEventModel.FromSseFrame(frame);
            if (_dispatcher is not null)
            {
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
                            await onEvent(model).ConfigureAwait(true);
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
                    // A dispatcher can disappear during window shutdown. Do not
                    // silently lose the durable event; deliver it on the caller
                    // context instead.
                    await onEvent(model).ConfigureAwait(false);
                }
            }
            else
            {
                await onEvent(model).ConfigureAwait(false);
            }
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
