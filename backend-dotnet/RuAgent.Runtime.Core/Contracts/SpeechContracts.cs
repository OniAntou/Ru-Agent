using System.Text;

namespace RuAgent.Runtime.Core.Contracts;

public sealed record SpeechRequest
{
    public SpeechRequest(ReadOnlyMemory<byte> wavBytes, int sampleRateHz, int channels, string language)
    {
        if (wavBytes.IsEmpty || wavBytes.Length > RuntimeLimits.MaxSpeechAudioBytes)
        {
            throw new ArgumentException("Speech audio is invalid.", nameof(wavBytes));
        }

        if (sampleRateHz != 16_000 || channels != 1)
        {
            throw new ArgumentException("Speech audio must be mono 16 kHz.", nameof(sampleRateHz));
        }

        if (string.IsNullOrWhiteSpace(language) || Encoding.UTF8.GetByteCount(language) > 32)
        {
            throw new ArgumentException("Speech language is invalid.", nameof(language));
        }

        WavBytes = wavBytes;
        SampleRateHz = sampleRateHz;
        Channels = channels;
        Language = language;
    }

    public ReadOnlyMemory<byte> WavBytes { get; }
    public int SampleRateHz { get; }
    public int Channels { get; }
    public string Language { get; }
}
