namespace RuApp.Services;

public interface IVoicePlaybackDevice : IDisposable
{
    bool IsPlaying { get; }
    Task PlayAsync(byte[] audio, CancellationToken cancellationToken, Action? onStarted = null);
    void Stop();
}
