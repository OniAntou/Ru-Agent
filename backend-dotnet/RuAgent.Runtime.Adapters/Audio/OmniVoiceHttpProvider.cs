using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Core.Contracts;

namespace RuAgent.Runtime.Adapters.Audio;

public sealed class OmniVoiceHttpProvider : IVoiceProvider, IVoiceLifecycle, IVoiceReadiness
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
    private readonly HttpClient _httpClient;
    private readonly OmniVoiceProviderOptions _options;
    private readonly OmniVoiceProcessController? _processController;
    private int _ready;

    public OmniVoiceHttpProvider(
        HttpClient httpClient,
        OmniVoiceProviderOptions options,
        OmniVoiceProcessController? processController = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _processController = processController;
    }

    public bool IsReady => _processController is null
        || (_processController.IsRunning && Volatile.Read(ref _ready) != 0);

    public async Task WarmupAsync(string voiceProfile, CancellationToken cancellationToken)
    {
        var profile = MapProfile(voiceProfile);
        await EnsureProviderStartedAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendJsonAsync(
            HttpMethod.Post,
            "warmup",
            new JsonObject { ["voice_profile"] = profile },
            cancellationToken,
            _options.StartupTimeout).ConfigureAwait(false);
        if (_processController is not null)
        {
            if (!TryReadBoolean(response, "ready", out var ready) || !ready)
            {
                throw new OmniVoiceProviderException("voice_unavailable", retryable: true);
            }

            Volatile.Write(ref _ready, 1);
        }
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        // A process that was never used must not be started merely to release it.
        // Composition owns the final process stop during Worker shutdown.
        if (_processController is not null && !_processController.IsRunning)
        {
            return;
        }

        _ = await SendJsonAsync(HttpMethod.Post, "release", new JsonObject(), cancellationToken)
            .ConfigureAwait(false);
        Volatile.Write(ref _ready, 0);
    }

    public async Task<VoiceAudio> SynthesizeAsync(
        VoiceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = MapProfile(request.VoiceProfile);
        await EnsureProviderStartedAsync(cancellationToken).ConfigureAwait(false);
        await EnsureProviderReadyAsync(profile, cancellationToken).ConfigureAwait(false);
        var response = await SendAsync(
            HttpMethod.Post,
            "tts",
            new JsonObject
            {
                ["text"] = request.Text.Trim(),
                ["voice_profile"] = profile,
                ["speed"] = request.Speed,
                ["voice_style_id"] = request.VoiceStyleId,
            },
            expectAudio: true,
            cancellationToken).ConfigureAwait(false);
        var wav = response.Body;
        var format = VoiceWavReader.Validate(wav, _options.MaxResponseBytes);
        return new VoiceAudio(wav, format.SampleRateHz, format.Channels);
    }

    public async IAsyncEnumerable<VoiceChunk> StreamAsync(
        VoiceRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = MapProfile(request.VoiceProfile);
        await EnsureProviderStartedAsync(cancellationToken).ConfigureAwait(false);
        await EnsureProviderReadyAsync(profile, cancellationToken).ConfigureAwait(false);
        var response = await SendAsync(
            HttpMethod.Post,
            "tts_stream",
            new JsonObject
            {
                ["text"] = request.Text.Trim(),
                ["voice_profile"] = profile,
                ["speed"] = request.Speed,
                ["voice_style_id"] = request.VoiceStyleId,
            },
            expectAudio: true,
            cancellationToken).ConfigureAwait(false);
        var wav = VoiceWavReader.Validate(response.Body, _options.MaxResponseBytes);
        _ = wav;
        yield return new VoiceChunk("audio", 0, response.Body);
        yield return new VoiceChunk("ended", 1);
    }

    private async Task<JsonObject> SendJsonAsync(
        HttpMethod method,
        string path,
        JsonObject? payload,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        var response = await SendAsync(
                method,
                path,
                payload,
                expectAudio: false,
                cancellationToken,
                requestTimeout)
            .ConfigureAwait(false);
        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(response.Body)) as JsonObject
                ?? new JsonObject();
        }
        catch (Exception error) when (error is DecoderFallbackException or JsonException)
        {
            throw new OmniVoiceProviderException("invalid_response", retryable: false, error);
        }
    }

    private async Task EnsureProviderStartedAsync(CancellationToken cancellationToken)
    {
        if (_processController is null)
        {
            return;
        }

        try
        {
            await _processController.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new OmniVoiceProviderException("voice_unavailable", retryable: true, error);
        }
    }

    private async Task EnsureProviderReadyAsync(
        string profile,
        CancellationToken cancellationToken)
    {
        // A provider supplied by a host or test owns its own readiness contract.
        // Only the process-backed composition needs the loopback health probe.
        if (_processController is null)
        {
            return;
        }

        // A previously successful request must not keep advertising readiness
        // while a replacement process is starting or a later health check has
        // observed a failure.
        Volatile.Write(ref _ready, 0);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var remaining = _options.StartupTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new OmniVoiceProviderException("voice_unavailable", retryable: true);
            }

            JsonObject status;
            try
            {
                status = await SendJsonAsync(
                    HttpMethod.Get,
                    "status",
                    payload: null,
                    cancellationToken,
                    remaining < _options.ReadinessTimeout ? remaining : _options.ReadinessTimeout)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (OmniVoiceProviderException error) when (
                error.Code is "voice_unavailable" or "timeout" or "voice_failed")
            {
                if (stopwatch.Elapsed >= _options.StartupTimeout)
                {
                    throw new OmniVoiceProviderException("voice_unavailable", retryable: true, error);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (!TryReadBoolean(status, "model_loaded", out var loaded))
            {
                throw new OmniVoiceProviderException("invalid_response", retryable: false);
            }

            if (loaded)
            {
                Volatile.Write(ref _ready, 1);
                return;
            }

            try
            {
                remaining = _options.StartupTimeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new OmniVoiceProviderException("voice_unavailable", retryable: true);
                }

                var warmup = await SendJsonAsync(
                    HttpMethod.Post,
                    "warmup",
                    new JsonObject { ["voice_profile"] = profile },
                    cancellationToken,
                    remaining).ConfigureAwait(false);
                if (TryReadBoolean(warmup, "ready", out var ready) && ready)
                {
                    Volatile.Write(ref _ready, 1);
                    return;
                }

                throw new OmniVoiceProviderException("voice_unavailable", retryable: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (OmniVoiceProviderException error)
            {
                throw error.Code == "voice_unavailable"
                    ? error
                    : new OmniVoiceProviderException("voice_unavailable", retryable: true, error);
            }
        }
    }

    private async Task<ProviderResponse> SendAsync(
        HttpMethod method,
        string path,
        JsonObject? payload,
        bool expectAudio,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(_options.BaseUri, path));
        if (payload is not null)
        {
            request.Content = new StringContent(
                payload.ToJsonString(JsonOptions),
                Encoding.UTF8,
                "application/json");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout ?? _options.RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OmniVoiceProviderException("timeout", retryable: true);
        }
        catch (HttpRequestException error)
        {
            throw new OmniVoiceProviderException("voice_unavailable", retryable: true, error);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new OmniVoiceProviderException(
                    response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity
                        ? "invalid_request"
                        : "voice_failed",
                    retryable: (int)response.StatusCode >= 500);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (expectAudio
                && contentType is not ("audio/wav" or "audio/x-wav"))
            {
                throw new OmniVoiceProviderException("invalid_response", retryable: false);
            }

            byte[] body;
            try
            {
                body = await ReadBoundedAsync(response.Content, _options.MaxResponseBytes, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new OmniVoiceProviderException("timeout", retryable: true);
            }
            catch (HttpRequestException error)
            {
                throw new OmniVoiceProviderException("voice_unavailable", retryable: true, error);
            }

            return new ProviderResponse(contentType, body);
        }
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
                break;
            }

            if (output.Length + read > maximum)
            {
                throw new OmniVoiceProviderException("response_too_large", retryable: false);
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return output.ToArray();
    }

    private static string MapProfile(string value) => value.Trim() switch
    {
        "ru" or "Ru (OmniVoice)" => "ru",
        "ai_vy" or "Ái Vy (OmniVoice)" => "ai_vy",
        _ => throw new OmniVoiceProviderException("unsupported_voice", retryable: false),
    };

    private sealed record ProviderResponse(string ContentType, byte[] Body);

    private static bool TryReadBoolean(JsonObject payload, string key, out bool value)
    {
        if (payload[key] is JsonValue node && node.TryGetValue<bool>(out value))
        {
            return true;
        }

        value = false;
        return false;
    }
}
