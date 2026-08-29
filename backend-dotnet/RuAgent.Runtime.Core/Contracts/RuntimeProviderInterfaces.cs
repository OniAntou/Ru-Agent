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

public interface ISpeechRecognizer
{
    Task<string> RecognizeAsync(
        SpeechRequest request,
        CancellationToken cancellationToken);
}

public interface IVtubeStudioClient
{
    Task<VtsCatalog> ReadCatalogAsync(CancellationToken cancellationToken);

    Task TriggerAsync(
        IReadOnlyList<string> catalogCodes,
        CancellationToken cancellationToken);
}

public interface IObservationProvider
{
    Task<VisionObservation?> AnalyzeAsync(
        ObservationRequest request,
        CancellationToken cancellationToken);
}
