using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Core.Contracts;
using RuAgent.Runtime.Core.Lifecycle;

namespace RuAgent.Runtime.Adapters.Observation;

public sealed class GeminiVisionProvider : IObservationProvider
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HttpClient _httpClient;
    private readonly GeminiVisionOptions _options;
    private readonly RuntimeLeaseCoordinator? _leases;

    public GeminiVisionProvider(
        HttpClient httpClient,
        GeminiVisionOptions options,
        RuntimeLeaseCoordinator? leases = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _leases = leases;
        _options.Validate();
    }

    public async Task<VisionObservation?> AnalyzeAsync(
        ObservationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RuntimeLease? lease = null;
        if (_leases is not null && !_leases.TryAcquire(ResourceKind.Observation, out lease))
        {
            throw new ObservationProviderException(
                "observation_skipped",
                retryable: false,
                reason: "generation_busy");
        }

        try
        {
            return await AnalyzeWithLeaseAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<VisionObservation?> AnalyzeWithLeaseAsync(
        ObservationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new ObservationProviderException(
                "observation_skipped",
                retryable: false,
                reason: "missing_api_key");
        }

        var models = new[] { request.ModelName }
            .Concat(_options.Models.Where(model => !string.Equals(model, request.ModelName, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var model in models)
        {
            try
            {
                var response = await SendAsync(request, model, cancellationToken).ConfigureAwait(false);
                return VisionObservationParser.Parse(response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ObservationProviderException error) when (error.Code == "observation_skipped")
            {
                throw;
            }
            catch (ObservationProviderException)
            {
                // Try the next model in the configured cascade.
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException)
            {
                // Keep provider and transport details out of the Worker boundary.
            }
        }

        throw new ObservationProviderException("observation_failed", retryable: true);
    }

    private async Task<JsonObject> SendAsync(
        ObservationRequest request,
        string model,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(
            $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent",
            UriKind.Absolute);
        var payload = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["parts"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["inline_data"] = new JsonObject
                            {
                                ["mime_type"] = "image/jpeg",
                                ["data"] = Convert.ToBase64String(request.JpegBytes.Span),
                            },
                        },
                        new JsonObject { ["text"] = request.Prompt },
                    },
                },
            },
            ["generationConfig"] = GenerationConfig(model),
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("x-goog-api-key", _options.ApiKey.Trim());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ObservationProviderException("observation_timeout", retryable: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException error)
        {
            throw new ObservationProviderException("observation_unavailable", retryable: true, error);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ObservationProviderException(
                    "observation_failed",
                    retryable: response.StatusCode == HttpStatusCode.TooManyRequests
                        || (int)response.StatusCode >= 500);
            }

            byte[] body;
            try
            {
                body = await ReadBoundedAsync(response.Content, _options.MaxResponseBytes, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ObservationProviderException("observation_timeout", retryable: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException error)
            {
                throw new ObservationProviderException("observation_unavailable", retryable: true, error);
            }

            try
            {
                return JsonNode.Parse(StrictUtf8.GetString(body)) as JsonObject
                    ?? throw new JsonException();
            }
            catch (Exception error) when (error is DecoderFallbackException or JsonException)
            {
                throw new ObservationProviderException("malformed_payload", retryable: false, error);
            }
        }
    }

    private static JsonObject GenerationConfig(string model)
    {
        var config = new JsonObject { ["responseMimeType"] = "application/json" };
        if (!model.StartsWith("gemini-3.6-", StringComparison.OrdinalIgnoreCase)
            && !model.StartsWith("gemini-3.7-", StringComparison.OrdinalIgnoreCase))
        {
            config["temperature"] = 0.7;
        }

        return config;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximum)
            {
                throw new ObservationProviderException("response_too_large", retryable: false);
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
