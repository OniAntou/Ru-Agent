using System.Net.Http;
using RuAgent.Runtime.Core.Contracts;

namespace RuAgent.Runtime.Adapters.Speech;

public sealed record GoogleSpeechRecognizerOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public int ExpectedSampleRateHz { get; init; } = 16_000;
    public int ExpectedChannels { get; init; } = 1;
    public string Language { get; init; } = "vi-VN";

    public void Validate()
    {
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromSeconds(15))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        if (ExpectedSampleRateHz != 16_000 || ExpectedChannels != 1)
        {
            throw new ArgumentException("Google speech input must be mono 16 kHz.");
        }

        if (!string.Equals(Language, "vi-VN", StringComparison.Ordinal))
        {
            throw new ArgumentException("Google speech language is invalid.", nameof(Language));
        }
    }
}

public sealed class GoogleSpeechRecognizer : ISpeechRecognizer
{
    private const int MaximumTranscriptCharacters = 64 * 1024;
    private readonly IGoogleSpeechTransport _transport;
    private readonly GoogleSpeechRecognizerOptions _options;

    public GoogleSpeechRecognizer(
        IGoogleSpeechTransport transport,
        GoogleSpeechRecognizerOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new GoogleSpeechRecognizerOptions();
        _options.Validate();
    }

    public async Task<string> RecognizeAsync(
        SpeechRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        WavAudioValidator.Validate(
            request.WavBytes.Span,
            _options.ExpectedSampleRateHz,
            _options.ExpectedChannels);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        string? transcript;
        try
        {
            transcript = await _transport.RecognizeAsync(
                request.WavBytes,
                _options.Language,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SpeechProviderException("speech_timeout", retryable: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GoogleSpeechTransportException error)
        {
            throw MapTransportFailure(error);
        }
        catch (HttpRequestException error)
        {
            throw new SpeechProviderException("speech_unavailable", retryable: true, error);
        }
        catch (TimeoutException error)
        {
            throw new SpeechProviderException("speech_timeout", retryable: true, error);
        }
        catch (SpeechProviderException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new SpeechProviderException("speech_failed", retryable: true, error);
        }

        var normalized = NormalizeTranscript(transcript);
        return normalized;
    }

    private static string NormalizeTranscript(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            throw new SpeechProviderException("speech_no_match", retryable: false);
        }

        if (transcript.Length > MaximumTranscriptCharacters)
        {
            throw new SpeechProviderException("speech_response_too_large", retryable: false);
        }

        var normalized = string.Join(
            " ",
            transcript.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length == 0
            ? throw new SpeechProviderException("speech_no_match", retryable: false)
            : normalized;
    }

    private static SpeechProviderException MapTransportFailure(GoogleSpeechTransportException error) =>
        error.Failure switch
        {
            SpeechTransportFailure.NoMatch => new SpeechProviderException("speech_no_match", retryable: false),
            SpeechTransportFailure.Timeout => new SpeechProviderException("speech_timeout", retryable: true),
            SpeechTransportFailure.Unavailable => new SpeechProviderException("speech_unavailable", retryable: true),
            _ => new SpeechProviderException("speech_failed", retryable: true),
        };
}
