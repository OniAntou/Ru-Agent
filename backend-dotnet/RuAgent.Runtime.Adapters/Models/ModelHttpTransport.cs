namespace RuAgent.Runtime.Adapters.Models;

/// <summary>
/// Bounds the request phase through response headers while leaving streaming
/// response bodies to the caller token and the SSE idle watchdog.
/// </summary>
public static class ModelHttpTransport
{
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(120);

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(request);
        var timeout = requestTimeout ?? DefaultRequestTimeout;
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCts.CancelAfter(timeout);
        try
        {
            return await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelProviderException("timeout", retryable: true, error);
        }
        catch (HttpRequestException error)
        {
            throw new ModelProviderException("transport_error", retryable: true, error);
        }
    }
}
