namespace RuAgent.Runtime.Core.Contracts;

public interface IVoiceProvider
{
    Task<VoiceAudio> SynthesizeAsync(
        VoiceRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<VoiceChunk> StreamAsync(
        VoiceRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional lifecycle operations exposed by providers that own a warmable
/// inference process. Keeping this separate from <see cref="IVoiceProvider"/>
/// lets deterministic providers implement synthesis without pretending to own
/// process lifecycle.
/// </summary>
public interface IVoiceLifecycle
{
    Task WarmupAsync(string voiceProfile, CancellationToken cancellationToken);

    Task ReleaseAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Optional readiness state for providers that expose a process-backed
/// inference engine. A provider must not advertise itself as available until
/// its bounded health/readiness check has completed successfully.
/// </summary>
public interface IVoiceReadiness
{
    bool IsReady { get; }
}

public interface ISpeechRecognizer
{
    Task<string> RecognizeAsync(
        SpeechRequest request,
        CancellationToken cancellationToken);
}
