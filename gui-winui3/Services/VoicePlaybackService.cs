using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace RuApp.Services
{
    public sealed class VoicePlaybackService : IDisposable
    {
        private readonly ApiService _apiService;
        private MediaPlayer? _player;
        private InMemoryRandomAccessStream? _stream;
        private CancellationTokenSource? _cts;
        private VoiceStreamClient? _streamClient;
        private VoicePlaybackQueue? _playbackQueue;
        private string? _activeRequestId;
        private bool _vtsSpeaking;
        private bool _disposed;

        public string VoiceLabel { get; set; } = "Ru (OmniVoice)";
        public double VoiceSpeed { get; set; } = 1.0;
        public bool AutoReadReplies { get; set; } = true;

        public bool IsPlaying => _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

        public VoicePlaybackService(ApiService apiService)
        {
            _apiService = apiService;
        }

        public async Task RefreshSettingsAsync()
        {
            var voice = await _apiService.GetVoiceStatusAsync();
            if (voice == null) return;
            VoiceLabel = voice.VoiceLabel;
            VoiceSpeed = voice.VoiceSpeed;
            AutoReadReplies = voice.AutoReadReplies;
        }

        public async Task SpeakTextAsync(string text, bool interrupt = true)
        {
            if (_disposed || string.IsNullOrWhiteSpace(text)) return;

            if (interrupt)
            {
                StopVoice();
            }
            else if (_activeRequestId is not null || IsPlaying)
            {
                return;
            }

            string requestId = Guid.NewGuid().ToString("N");
            var cts = new CancellationTokenSource();
            var streamClient = _apiService.CreateVoiceStreamClient();
            var playbackQueue = new VoicePlaybackQueue(PlayChunkAsync);
            _activeRequestId = requestId;
            _cts = cts;
            _streamClient = streamClient;
            _playbackQueue = playbackQueue;

            bool receivedAudio = false;
            VoiceStreamTerminalEvent? terminal = null;
            try
            {
                await playbackQueue.StartAsync(requestId);
                await streamClient.StartAsync(
                    requestId,
                    text.Trim(),
                    VoiceLabel,
                    VoiceSpeed,
                    async chunk =>
                    {
                        receivedAudio = true;
                        await playbackQueue.EnqueueAsync(requestId, chunk, cts.Token);
                    },
                    streamEvent =>
                    {
                        terminal = streamEvent;
                        return Task.CompletedTask;
                    },
                    cts.Token);

                if (terminal?.Type == "ended")
                {
                    await playbackQueue.CompleteAsync(requestId);
                    await ReportPlaybackAsync(requestId, "ended");
                }
                else
                {
                    playbackQueue.Cancel(requestId);
                    if (!receivedAudio && !cts.IsCancellationRequested && IsCurrentRequest(requestId))
                    {
                        await PlayFallbackAsync(text.Trim(), cts.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                playbackQueue.Cancel(requestId);
            }
            catch (Exception ex)
            {
                playbackQueue.Cancel(requestId);
                Debug.WriteLine($"[VoicePlayback] Stream failed: {ex.Message}");

                if (receivedAudio && !cts.IsCancellationRequested)
                {
                    await ReportPlaybackAsync(requestId, "failed");
                }

                if (!receivedAudio && !cts.IsCancellationRequested && IsCurrentRequest(requestId))
                {
                    await PlayFallbackAsync(text.Trim(), cts.Token);
                }
            }
            finally
            {
                if (IsCurrentRequest(requestId) && _vtsSpeaking)
                {
                    _vtsSpeaking = false;
                    await ResetVtsSpeakingAsync();
                }
                playbackQueue.Dispose();
                streamClient.Dispose();
                if (IsCurrentRequest(requestId))
                {
                    _playbackQueue = null;
                    _streamClient = null;
                    _activeRequestId = null;
                    _cts = null;
                    cts.Dispose();
                }
                else
                {
                    cts.Dispose();
                }
            }
        }

        public void StopVoice()
        {
            string? requestId = _activeRequestId;
            CancellationTokenSource? cts = _cts;
            VoiceStreamClient? streamClient = _streamClient;
            VoicePlaybackQueue? playbackQueue = _playbackQueue;

            _activeRequestId = null;
            _cts = null;
            _streamClient = null;
            _playbackQueue = null;

            if (requestId is not null)
            {
                playbackQueue?.Cancel(requestId);
                if (streamClient is not null)
                {
                    _ = CancelStreamAsync(streamClient, requestId);
                }
            }

            cts?.Cancel();
            playbackQueue?.Dispose();
            if (_vtsSpeaking)
            {
                _vtsSpeaking = false;
                _ = ResetVtsSpeakingAsync();
            }
            CleanupPlayer();
        }

        private async Task PlayChunkAsync(VoiceAudioChunk chunk, CancellationToken cancellationToken)
        {
            await PlayWavAsync(chunk.Audio, cancellationToken, chunk.RequestId);
        }

        private async Task PlayFallbackAsync(string text, CancellationToken cancellationToken)
        {
            byte[]? audio = await _apiService.SynthesizeTtsAsync(text, VoiceLabel, VoiceSpeed);
            if (audio is null || audio.Length == 0 || cancellationToken.IsCancellationRequested)
            {
                return;
            }
            await PlayWavAsync(audio, cancellationToken);
        }

        private async Task PlayWavAsync(
            byte[] audio,
            CancellationToken cancellationToken,
            string? requestId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var player = new MediaPlayer { AutoPlay = false };
            var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream);
            writer.WriteBytes(audio);
            await writer.StoreAsync();
            writer.DetachStream();
            stream.Seek(0);

            var opened = NewCompletionSource();
            var ended = NewCompletionSource();
            player.MediaOpened += (_, _) => opened.TrySetResult(true);
            player.MediaEnded += (_, _) => ended.TrySetResult(true);
            player.MediaFailed += (_, _) =>
            {
                var error = new InvalidOperationException("The audio chunk could not be played.");
                opened.TrySetException(error);
                ended.TrySetException(error);
            };

            _player = player;
            _stream = stream;
            try
            {
                player.Source = MediaSource.CreateFromStream(stream, "audio/wav");
                await opened.Task.WaitAsync(cancellationToken);
                if (!_vtsSpeaking)
                {
                    bool ok = await _apiService.SetVtsSpeakingAsync(true);
                    if (!ok)
                    {
                        throw new InvalidOperationException("VTS speaking state was not accepted.");
                    }
                    _vtsSpeaking = true;
                    if (requestId is not null)
                    {
                        await ReportPlaybackAsync(requestId, "started");
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                player.Play();
                await ended.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                if (requestId is not null)
                {
                    await ReportPlaybackAsync(requestId, "failed");
                }
                throw;
            }
            finally
            {
                if (ReferenceEquals(_player, player))
                {
                    _player = null;
                }
                if (ReferenceEquals(_stream, stream))
                {
                    _stream = null;
                }
                try
                {
                    player.Pause();
                    player.Source = null;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VoicePlayback] Player cleanup failed: {ex.Message}");
                }
                player.Dispose();
                stream.Dispose();
            }
        }

        private bool IsCurrentRequest(string requestId) => _activeRequestId == requestId;

        private static TaskCompletionSource<bool> NewCompletionSource()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static async Task CancelStreamAsync(VoiceStreamClient client, string requestId)
        {
            try
            {
                await client.AcknowledgePlaybackAsync(requestId, "cancelled");
                await client.CancelAsync(requestId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoicePlayback] Stream cancellation failed: {ex.Message}");
            }
        }

        private async Task ReportPlaybackAsync(string requestId, string state)
        {
            VoiceStreamClient? client = _streamClient;
            if (client is null)
            {
                return;
            }
            try
            {
                await client.AcknowledgePlaybackAsync(requestId, state);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoicePlayback] Playback acknowledgement failed: {ex.Message}");
            }
        }

        private async Task ResetVtsSpeakingAsync()
        {
            try
            {
                await _apiService.SetVtsSpeakingAsync(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoicePlayback] Reset VTS speaking failed: {ex.Message}");
            }
        }

        private void CleanupPlayer()
        {
            var player = _player;
            _player = null;
            if (player == null) return;
            try
            {
                player.Pause();
                player.Source = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoicePlayback] Player cleanup failed: {ex.Message}");
            }
            player.Dispose();
            _stream?.Dispose();
            _stream = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopVoice();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            StopVoice();
            if (_vtsSpeaking)
            {
                _vtsSpeaking = false;
                await ResetVtsSpeakingAsync();
            }
        }
    }
}
