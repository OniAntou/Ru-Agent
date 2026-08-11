namespace RuBackend.Worker;

public interface IVoiceProtocolClient
{
    IAsyncEnumerable<WorkerEvent> StreamVoiceAsync(
        WorkerVoiceStreamRequest request,
        CancellationToken cancellationToken);

    Task SendVoiceSignalAsync(
        WorkerVoiceSignal signal,
        CancellationToken cancellationToken);
}
