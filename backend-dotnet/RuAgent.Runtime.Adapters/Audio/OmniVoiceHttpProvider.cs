using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Core.Contracts;

namespace RuAgent.Runtime.Adapters.Audio;

public sealed class OmniVoiceHttpProvider : IVoiceProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
    private readonly HttpClient _httpClient;
    private readonly OmniVoiceProviderOptions _options;

    public OmniVoiceHttpProvider(HttpClient httpClient, OmniVoiceProviderOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task WarmupAsync(string voiceProfile, CancellationToken cancellationToken)
    {
        var profile = MapProfile(voiceProfile);
        _ = await SendJsonAsync(
            HttpMethod.Post,
            "warmup",
            new JsonObject { ["voice_profile"] = profile },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        _ = await SendJsonAsync(HttpMethod.Post, "release", new JsonObject(), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<VoiceAudio> SynthesizeAsync(
        VoiceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await SendAsync(
            HttpMethod.Post,
            "tts",
            new JsonObject
            {
                ["text"] = request.Text.Trim(),
                ["voice_profile"] = MapProfile(request.VoiceProfile),
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
        var response = await SendAsync(
            HttpMethod.Post,
            "tts_stream",
            new JsonObject
            {
                ["text"] = request.Text.Trim(),
                ["voice_profile"] = MapProfile(request.VoiceProfile),
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
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(method, path, payload, expectAudio: false, cancellationToken)
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

    private async Task<ProviderResponse> SendAsync(
        HttpMethod method,
        string path,
        JsonObject payload,
        bool expectAudio,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_options.BaseUri, path))
        {
            Content = new StringContent(
                payload.ToJsonString(JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

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
}
