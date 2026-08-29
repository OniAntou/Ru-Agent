using System.Text;

namespace RuAgent.Runtime.Core.Contracts;

public sealed record VoiceRequest
{
    public VoiceRequest(string text, string voiceProfile, double speed, string voiceStyleId)
    {
        if (string.IsNullOrWhiteSpace(text)
            || Encoding.UTF8.GetByteCount(text) > RuntimeLimits.MaxVoiceTextBytes)
        {
            throw new ArgumentException("Voice text is invalid.", nameof(text));
        }

        if (string.IsNullOrWhiteSpace(voiceProfile) || voiceProfile.Length > 128)
        {
            throw new ArgumentException("Voice profile is invalid.", nameof(voiceProfile));
        }

        if (!double.IsFinite(speed) || speed is < 0.5 or > 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(speed));
        }

        if (!string.Equals(voiceStyleId, "original", StringComparison.Ordinal))
        {
            throw new ArgumentException("Only the original voice style is supported.", nameof(voiceStyleId));
        }

        Text = text;
        VoiceProfile = voiceProfile;
        Speed = speed;
        VoiceStyleId = voiceStyleId;
    }

    public string Text { get; }
    public string VoiceProfile { get; }
    public double Speed { get; }
    public string VoiceStyleId { get; }
}

public sealed record VoiceAudio
{
    public VoiceAudio(ReadOnlyMemory<byte> wavBytes, int sampleRateHz, int channels)
    {
        if (wavBytes.IsEmpty || wavBytes.Length > RuntimeLimits.MaxVoiceAudioBytes)
        {
            throw new ArgumentException("Voice audio is invalid.", nameof(wavBytes));
        }

        if (sampleRateHz != 24_000)
        {
            throw new ArgumentException("Voice audio must be 24 kHz.", nameof(sampleRateHz));
        }

        if (channels != 1)
        {
            throw new ArgumentException("Voice audio must be mono.", nameof(channels));
        }

        WavBytes = wavBytes;
        SampleRateHz = sampleRateHz;
        Channels = channels;
    }

    public ReadOnlyMemory<byte> WavBytes { get; }
    public int SampleRateHz { get; }
    public int Channels { get; }
}

public sealed record VoiceChunk
{
    public VoiceChunk(string state, long sequence, ReadOnlyMemory<byte> audio = default)
    {
        if (state is not ("audio" or "ended" or "cancelled" or "failed"))
        {
            throw new ArgumentException("Voice chunk state is invalid.", nameof(state));
        }

        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (state == "audio" && audio.IsEmpty)
        {
            throw new ArgumentException("Audio chunks must contain bytes.", nameof(audio));
        }

        if (state != "audio" && !audio.IsEmpty)
        {
            throw new ArgumentException("Terminal chunks cannot contain audio.", nameof(audio));
        }

        State = state;
        Sequence = sequence;
        Audio = audio;
    }

    public string State { get; }
    public long Sequence { get; }
    public ReadOnlyMemory<byte> Audio { get; }
}
